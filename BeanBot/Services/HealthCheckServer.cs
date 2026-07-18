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
        internal const int DefaultMaximumConcurrentClients = 64;
        internal const int DefaultMaximumTrackedRateLimitClients = 4096;
        private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly HealthCheckOptions _options;
        private readonly DiscordSocketClient _discordClient;
        private readonly DiscordConnectionHealth _discordConnectionHealth;
        private readonly TimeSpan _requestTimeout;
        private readonly BoundedClientRateLimiter _rateLimiter;
        private readonly SemaphoreSlim _clientCapacity;
        private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
        private readonly CancellationTokenSource _shutdown = new();
        private int _nextClientTaskId;
        private int _activeClientHandlers;
        private int _peakActiveClientHandlers;
        private int _disposed;
        private TcpListener _listener;
        private Task _listenerLoop;

        public HealthCheckServer(
            HealthCheckOptions options,
            DiscordSocketClient discordClient,
            DiscordConnectionHealth discordConnectionHealth,
            TimeSpan? requestTimeout = null,
            int maximumConcurrentClients = DefaultMaximumConcurrentClients,
            int maximumTrackedRateLimitClients = DefaultMaximumTrackedRateLimitClients)
        {
            if (maximumConcurrentClients <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumConcurrentClients));
            }

            _options = options ?? throw new ArgumentNullException(nameof(options));
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
            _discordConnectionHealth = discordConnectionHealth ?? throw new ArgumentNullException(nameof(discordConnectionHealth));
            _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
            _rateLimiter = new BoundedClientRateLimiter(options.MinimumPollInterval, maximumTrackedRateLimitClients);
            _clientCapacity = new SemaphoreSlim(maximumConcurrentClients, maximumConcurrentClients);
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

            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(HealthCheckServer));
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
        internal int ActiveClientTaskCount => _clientTasks.Count;
        internal int ActiveClientHandlerCount => Volatile.Read(ref _activeClientHandlers);
        internal int PeakActiveClientHandlers => Volatile.Read(ref _peakActiveClientHandlers);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_listener is null)
            {
                _clientCapacity.Dispose();
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

            try
            {
                await Task.WhenAll(_clientTasks.Values);
            }
            catch (Exception exception)
            {
                // Client handlers normally absorb connection failures. A task escaping
                // faulted indicates an implementation failure and should remain visible.
                Log.Error(exception, "A health check client task failed during shutdown.");
            }
            finally
            {
                RemoveCompletedClientTasks(logFaults: false);
                _clientCapacity.Dispose();
                _shutdown.Dispose();
            }
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    RemoveCompletedClientTasks();

                    if (!_clientCapacity.Wait(0))
                    {
                        // Capacity is enforced before a handler task is created. Closing
                        // here bounds sockets, tasks, and queued work during connection bursts.
                        client.Dispose();
                        client = null;
                        continue;
                    }

                    var taskId = Interlocked.Increment(ref _nextClientTaskId);
                    _clientTasks[taskId] = HandleClientWithCapacityAsync(client, cancellationToken);
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

        private async Task HandleClientWithCapacityAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var activeHandlers = Interlocked.Increment(ref _activeClientHandlers);
            UpdatePeakActiveClientHandlers(activeHandlers);
            try
            {
                await HandleClientAsync(client, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeClientHandlers);
                _clientCapacity.Release();
            }
        }

        private void UpdatePeakActiveClientHandlers(int activeHandlers)
        {
            var observedPeak = Volatile.Read(ref _peakActiveClientHandlers);
            while (activeHandlers > observedPeak)
            {
                var priorPeak = Interlocked.CompareExchange(
                    ref _peakActiveClientHandlers,
                    activeHandlers,
                    observedPeak);
                if (priorPeak == observedPeak)
                {
                    return;
                }

                observedPeak = priorPeak;
            }
        }

        private void RemoveCompletedClientTasks(bool logFaults = true)
        {
            foreach (var clientTask in _clientTasks)
            {
                if (clientTask.Value.IsCompleted && _clientTasks.TryRemove(clientTask.Key, out var completedTask))
                {
                    if (logFaults && completedTask.IsFaulted)
                    {
                        // Reading Exception observes the fault before the task leaves the
                        // active collection. Routine connection errors are handled inside
                        // HandleClientAsync and never arrive here.
                        Log.Error(completedTask.Exception, "A health check client task failed unexpectedly.");
                    }
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

                    if (!TryExtractPath(target, out var path))
                    {
                        await WritePlainTextResponseAsync(stream, 400, "Bad Request", "Malformed request target.", false, requestToken);
                        return;
                    }

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
            => _rateLimiter.IsRateLimited(clientIdentifier, out retryAfterSeconds);

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

        internal static bool TryExtractPath(string requestTarget, out string path)
        {
            path = null;
            if (requestTarget == null)
            {
                return false;
            }

            var encodedPath = requestTarget;
            var queryIndex = encodedPath.IndexOf('?');
            if (queryIndex >= 0)
            {
                encodedPath = encodedPath.Substring(0, queryIndex);
            }

            for (var index = 0; index < encodedPath.Length; index++)
            {
                if (encodedPath[index] != '%')
                {
                    continue;
                }

                if (index + 2 >= encodedPath.Length ||
                    !Uri.IsHexDigit(encodedPath[index + 1]) ||
                    !Uri.IsHexDigit(encodedPath[index + 2]))
                {
                    return false;
                }

                index += 2;
            }

            try
            {
                path = string.IsNullOrWhiteSpace(encodedPath)
                    ? "/"
                    : Uri.UnescapeDataString(encodedPath);
                return true;
            }
            catch (UriFormatException)
            {
                return false;
            }
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
