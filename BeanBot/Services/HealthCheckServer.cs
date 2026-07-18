using BeanBot.Configuration;

using Discord.WebSocket;

using Serilog;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    public sealed class HealthCheckServer : IAsyncDisposable
    {
        internal const int MaxRequestLineLength = 2048;
        internal const int MaxHeaderLineLength = 8 * 1024;
        internal const int MaxHeaderCount = 100;
        internal const int MaxHeaderCharacters = 32 * 1024;
        private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly HealthCheckOptions _options;
        private readonly DiscordSocketClient _discordClient;
        private readonly DiscordConnectionHealth _discordConnectionHealth;
        private readonly TimeSpan _requestTimeout;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequestByClient = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
        private readonly CancellationTokenSource _shutdown = new();
        private int _nextClientTaskId;
        private TcpListener _listener;
        private Task _listenerLoop;

        public HealthCheckServer(
            HealthCheckOptions options,
            DiscordSocketClient discordClient,
            DiscordConnectionHealth discordConnectionHealth,
            TimeSpan? requestTimeout = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
            _discordConnectionHealth = discordConnectionHealth ?? throw new ArgumentNullException(nameof(discordConnectionHealth));
            _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        }

        public static HealthCheckServer Create(
            HealthCheckOptions options,
            DiscordSocketClient discordClient,
            DiscordConnectionHealth discordConnectionHealth)
        {
            return options.Enabled
                ? new HealthCheckServer(options, discordClient, discordConnectionHealth)
                : null;
        }

        public void Start()
        {
            if (!_options.Enabled)
            {
                return;
            }

            if (_listener is not null)
            {
                throw new InvalidOperationException("The health check server has already been started.");
            }

            _listener = new TcpListener(_options.BindAddress, _options.Port);
            _listener.Start();
            _listenerLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));

            Log.Information(
                "Health check endpoint listening on {BindAddress}:{Port}{Path} with a {RateLimitSeconds}s per-client poll limit",
                _options.BindAddress,
                _options.Port,
                _options.Path,
                (int)_options.MinimumPollInterval.TotalSeconds);

            if (string.IsNullOrWhiteSpace(_options.BearerToken)
                && !_options.BindAddress.Equals(IPAddress.Loopback)
                && !_options.BindAddress.Equals(IPAddress.IPv6Loopback))
            {
                Log.Warning(
                    "Health check endpoint is listening without a bearer token on {BindAddress}:{Port}{Path}",
                    _options.BindAddress,
                    _options.Port,
                    _options.Path);
            }
        }

        internal int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

        public async ValueTask DisposeAsync()
        {
            if (_listener is null)
            {
                _shutdown.Dispose();
                return;
            }

            _shutdown.Cancel();
            _listener.Stop();

            if (_listenerLoop is not null)
            {
                try
                {
                    await _listenerLoop;
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            await Task.WhenAll(_clientTasks.Values);

            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    var taskId = Interlocked.Increment(ref _nextClientTaskId);
                    _clientTasks[taskId] = HandleClientAsync(client, cancellationToken);
                    RemoveCompletedClientTasks();
                    client = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    client?.Dispose();
                    Log.Error(exception, "Health check listener failed while accepting a connection.");

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }

        private void RemoveCompletedClientTasks()
        {
            foreach (var clientTask in _clientTasks)
            {
                if (clientTask.Value.IsCompleted)
                {
                    _clientTasks.TryRemove(clientTask.Key, out _);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                var reader = new BoundedLineReader(stream);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(_requestTimeout);
                var requestToken = requestTimeout.Token;

                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;

                    string requestLine;
                    try
                    {
                        requestLine = await reader.ReadLineAsync(MaxRequestLineLength, requestToken);
                    }
                    catch (HttpLineTooLongException)
                    {
                        await WritePlainTextResponseAsync(stream, 414, "URI Too Long", "Request line is too long.", false, requestToken);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        return;
                    }

                    Dictionary<string, string> headers;
                    try
                    {
                        headers = await ReadHeadersAsync(reader, requestToken);
                    }
                    catch (HttpHeadersTooLargeException)
                    {
                        await WritePlainTextResponseAsync(stream, 431, "Request Header Fields Too Large", "Request headers are too large.", false, requestToken);
                        return;
                    }
                    catch (MalformedHttpHeaderException)
                    {
                        await WritePlainTextResponseAsync(stream, 400, "Bad Request", "Malformed HTTP request headers.", false, requestToken);
                        return;
                    }
                    if (!TryParseRequestLine(requestLine, out var method, out var target))
                    {
                        await WritePlainTextResponseAsync(stream, 400, "Bad Request", "Malformed HTTP request.", false, requestToken);
                        return;
                    }

                    var path = ExtractPath(target);
                    var isHeadRequest = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
                    if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && !isHeadRequest)
                    {
                        await WritePlainTextResponseAsync(
                            stream,
                            405,
                            "Method Not Allowed",
                            "Only GET and HEAD are supported.",
                            false,
                            requestToken,
                            ("Allow", "GET, HEAD"));
                        return;
                    }

                    if (!string.Equals(path, _options.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        await WritePlainTextResponseAsync(stream, 404, "Not Found", "Not Found", isHeadRequest, requestToken);
                        return;
                    }

                    if (!IsAuthorized(headers))
                    {
                        await WritePlainTextResponseAsync(
                            stream,
                            401,
                            "Unauthorized",
                            "Missing or invalid bearer token.",
                            isHeadRequest,
                            requestToken,
                            ("WWW-Authenticate", "Bearer"));
                        return;
                    }

                    var clientIdentifier = GetClientIdentifier(client);
                    if (IsRateLimited(clientIdentifier, out var retryAfter))
                    {
                        await WriteJsonResponseAsync(
                            stream,
                            429,
                            "Too Many Requests",
                            new
                            {
                                status = "rate_limited",
                                message = $"Wait {retryAfter} more seconds before polling {path} again.",
                                retryAfterSeconds = retryAfter
                            },
                            isHeadRequest,
                            requestToken,
                            ("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture)));
                        return;
                    }

                    var healthSnapshot = _discordConnectionHealth.CreateSnapshot(_discordClient);
                    await WriteJsonResponseAsync(
                        stream,
                        healthSnapshot.IsHealthy ? 200 : 503,
                        healthSnapshot.IsHealthy ? "OK" : "Service Unavailable",
                        new
                        {
                            status = healthSnapshot.IsHealthy ? "ok" : "unhealthy",
                            discordConnected = healthSnapshot.IsHealthy,
                            message = healthSnapshot.StatusMessage,
                            loginState = healthSnapshot.LoginState,
                            connectionState = healthSnapshot.ConnectionState,
                            lastReadyAtUtc = healthSnapshot.LastReadyAtUtc,
                            lastDisconnectedAtUtc = healthSnapshot.LastDisconnectedAtUtc,
                            lastDisconnectReason = healthSnapshot.LastDisconnectReason
                        },
                        isHeadRequest,
                        requestToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("Health check client exceeded the {TimeoutSeconds}s request timeout", _requestTimeout.TotalSeconds);
                    await TryWriteRequestTimeoutAsync(stream, cancellationToken);
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "Failed to process a health check request.");
                }
            }
        }

        private bool IsAuthorized(IReadOnlyDictionary<string, string> headers)
        {
            if (string.IsNullOrWhiteSpace(_options.BearerToken))
            {
                return true;
            }

            if (!headers.TryGetValue("Authorization", out var authorizationHeader)
                || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var providedToken = Encoding.UTF8.GetBytes(authorizationHeader["Bearer ".Length..].Trim());
            var expectedToken = Encoding.UTF8.GetBytes(_options.BearerToken);
            return CryptographicOperations.FixedTimeEquals(providedToken, expectedToken);
        }

        private bool IsRateLimited(string clientIdentifier, out int retryAfterSeconds)
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                if (!_lastRequestByClient.TryGetValue(clientIdentifier, out var lastRequestAt))
                {
                    if (_lastRequestByClient.TryAdd(clientIdentifier, now))
                    {
                        retryAfterSeconds = 0;
                        return false;
                    }

                    continue;
                }

                var nextAllowedAt = lastRequestAt.Add(_options.MinimumPollInterval);
                if (now >= nextAllowedAt)
                {
                    if (_lastRequestByClient.TryUpdate(clientIdentifier, now, lastRequestAt))
                    {
                        retryAfterSeconds = 0;
                        return false;
                    }

                    continue;
                }

                retryAfterSeconds = (int)Math.Ceiling((nextAllowedAt - now).TotalSeconds);
                return true;
            }
        }

        private static async Task<Dictionary<string, string>> ReadHeadersAsync(BoundedLineReader reader, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var totalCharacters = 0;
            var headerCount = 0;
            while (true)
            {
                string headerLine;
                try
                {
                    headerLine = await reader.ReadLineAsync(MaxHeaderLineLength, cancellationToken);
                }
                catch (HttpLineTooLongException exception)
                {
                    throw new HttpHeadersTooLargeException("An HTTP header line exceeded the configured limit.", exception);
                }

                if (headerLine == null)
                {
                    throw new MalformedHttpHeaderException("HTTP headers ended before the terminating blank line.");
                }

                if (headerLine.Length == 0)
                {
                    return headers;
                }

                totalCharacters += headerLine.Length;
                headerCount++;
                if (headerCount > MaxHeaderCount || totalCharacters > MaxHeaderCharacters)
                {
                    throw new HttpHeadersTooLargeException("HTTP request headers exceeded the configured limit.");
                }

                var separatorIndex = headerLine.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    throw new MalformedHttpHeaderException("An HTTP header was missing its name or separator.");
                }

                var headerName = headerLine.Substring(0, separatorIndex).Trim();
                var headerValue = headerLine.Substring(separatorIndex + 1).Trim();
                headers[headerName] = headerValue;
            }
        }

        private static async Task TryWriteRequestTimeoutAsync(NetworkStream stream, CancellationToken shutdownToken)
        {
            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            responseTimeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await WritePlainTextResponseAsync(
                    stream,
                    408,
                    "Request Timeout",
                    "The health request timed out.",
                    false,
                    responseTimeout.Token);
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is ObjectDisposedException ||
                exception is OperationCanceledException)
            {
            }
        }

        internal static bool TryParseRequestLine(string requestLine, out string method, out string target)
        {
            method = null;
            target = null;

            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 ||
                (!string.Equals(parts[2], "HTTP/1.1", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(parts[2], "HTTP/1.0", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            method = parts[0];
            target = parts[1];
            return true;
        }

        internal static string ExtractPath(string requestTarget)
        {
            var path = requestTarget;
            var queryIndex = path.IndexOf('?');
            if (queryIndex >= 0)
            {
                path = path.Substring(0, queryIndex);
            }

            return string.IsNullOrWhiteSpace(path)
                ? "/"
                : Uri.UnescapeDataString(path);
        }

        private static string GetClientIdentifier(TcpClient client)
        {
            return (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        }

        private static Task WritePlainTextResponseAsync(
            NetworkStream stream,
            int statusCode,
            string reasonPhrase,
            string body,
            bool suppressBody,
            CancellationToken cancellationToken,
            params (string Name, string Value)[] extraHeaders)
        {
            return WriteResponseAsync(stream, statusCode, reasonPhrase, "text/plain; charset=utf-8", body, suppressBody, cancellationToken, extraHeaders);
        }

        private static Task WriteJsonResponseAsync(
            NetworkStream stream,
            int statusCode,
            string reasonPhrase,
            object payload,
            bool suppressBody,
            CancellationToken cancellationToken,
            params (string Name, string Value)[] extraHeaders)
        {
            var body = JsonSerializer.Serialize(payload, JsonOptions);
            return WriteResponseAsync(stream, statusCode, reasonPhrase, "application/json; charset=utf-8", body, suppressBody, cancellationToken, extraHeaders);
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            int statusCode,
            string reasonPhrase,
            string contentType,
            string body,
            bool suppressBody,
            CancellationToken cancellationToken,
            params (string Name, string Value)[] extraHeaders)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            var responseBuilder = new StringBuilder()
                .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reasonPhrase).Append("\r\n")
                .Append("Content-Type: ").Append(contentType).Append("\r\n")
                .Append("Content-Length: ").Append(bodyBytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("Connection: close\r\n");

            foreach (var header in extraHeaders)
            {
                responseBuilder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
            }

            responseBuilder.Append("\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(responseBuilder.ToString());
            await stream.WriteAsync(headerBytes.AsMemory(), cancellationToken);
            if (!suppressBody && bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes.AsMemory(), cancellationToken);
            }

            await stream.FlushAsync(cancellationToken);
        }
    }

}
