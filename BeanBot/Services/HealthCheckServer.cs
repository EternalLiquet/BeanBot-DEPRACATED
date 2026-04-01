using BeanBot.Util;

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
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly HealthCheckServerOptions _options;
        private readonly DiscordSocketClient _discordClient;
        private readonly DiscordConnectionHealth _discordConnectionHealth;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequestByClient = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _shutdown = new();
        private TcpListener _listener;
        private Task _listenerLoop;

        public HealthCheckServer(
            HealthCheckServerOptions options,
            DiscordSocketClient discordClient,
            DiscordConnectionHealth discordConnectionHealth)
        {
            _options = options;
            _discordClient = discordClient;
            _discordConnectionHealth = discordConnectionHealth;
        }

        public static HealthCheckServer CreateFromSettings(
            DiscordSocketClient discordClient,
            DiscordConnectionHealth discordConnectionHealth)
        {
            var options = HealthCheckServerOptions.FromAppSettings();
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

            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().WaitAsync(cancellationToken);
                    _ = Task.Run(() => HandleClientAsync(client, cancellationToken));
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

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;

                    var requestLine = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        return;
                    }

                    var headers = await ReadHeadersAsync(reader, cancellationToken);
                    if (!TryParseRequestLine(requestLine, out var method, out var target))
                    {
                        await WritePlainTextResponseAsync(stream, 400, "Bad Request", "Malformed HTTP request.", false, cancellationToken);
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
                            cancellationToken,
                            ("Allow", "GET, HEAD"));
                        return;
                    }

                    if (!string.Equals(path, _options.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        await WritePlainTextResponseAsync(stream, 404, "Not Found", "Not Found", isHeadRequest, cancellationToken);
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
                            cancellationToken,
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
                            cancellationToken,
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
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
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

        private static async Task<Dictionary<string, string>> ReadHeadersAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var headerLine = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                if (string.IsNullOrEmpty(headerLine))
                {
                    return headers;
                }

                var separatorIndex = headerLine.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var headerName = headerLine.Substring(0, separatorIndex).Trim();
                var headerValue = headerLine.Substring(separatorIndex + 1).Trim();
                headers[headerName] = headerValue;
            }
        }

        private static bool TryParseRequestLine(string requestLine, out string method, out string target)
        {
            method = null;
            target = null;

            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            method = parts[0];
            target = parts[1];
            return true;
        }

        private static string ExtractPath(string requestTarget)
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
            var bodyText = suppressBody ? string.Empty : body ?? string.Empty;
            var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
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
            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes.AsMemory(), cancellationToken);
            }

            await stream.FlushAsync(cancellationToken);
        }
    }

    public sealed class HealthCheckServerOptions
    {
        public bool Enabled { get; init; }
        public IPAddress BindAddress { get; init; }
        public int Port { get; init; }
        public string Path { get; init; } = "/healthz";
        public string BearerToken { get; init; }
        public TimeSpan MinimumPollInterval { get; init; }

        public static HealthCheckServerOptions FromAppSettings()
        {
            if (!AppSettings.Settings.TryGetValue("healthCheckPort", out var portValue))
            {
                return new HealthCheckServerOptions
                {
                    Enabled = false
                };
            }

            if (!int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port < IPEndPoint.MinPort
                || port > IPEndPoint.MaxPort)
            {
                throw new InvalidOperationException(
                    $"Invalid value for {AppSettings.DescribeSetting("healthCheckPort")}: '{portValue}'. Expected a TCP port between {IPEndPoint.MinPort} and {IPEndPoint.MaxPort}.");
            }

            var bindAddress = IPAddress.Any;
            if (AppSettings.Settings.TryGetValue("healthCheckBindAddress", out var bindAddressValue)
                && !IPAddress.TryParse(bindAddressValue, out bindAddress))
            {
                throw new InvalidOperationException(
                    $"Invalid value for {AppSettings.DescribeSetting("healthCheckBindAddress")}: '{bindAddressValue}'. Expected an IP address such as 0.0.0.0 or 127.0.0.1.");
            }

            var minimumPollInterval = TimeSpan.FromSeconds(90);
            if (AppSettings.Settings.TryGetValue("healthCheckRateLimitSeconds", out var rateLimitValue))
            {
                if (!int.TryParse(rateLimitValue, NumberStyles.None, CultureInfo.InvariantCulture, out var rateLimitSeconds)
                    || rateLimitSeconds <= 0)
                {
                    throw new InvalidOperationException(
                        $"Invalid value for {AppSettings.DescribeSetting("healthCheckRateLimitSeconds")}: '{rateLimitValue}'. Expected a positive number of seconds.");
                }

                minimumPollInterval = TimeSpan.FromSeconds(rateLimitSeconds);
            }

            AppSettings.Settings.TryGetValue("healthCheckBearerToken", out var bearerToken);

            return new HealthCheckServerOptions
            {
                Enabled = true,
                BindAddress = bindAddress,
                Port = port,
                BearerToken = bearerToken,
                MinimumPollInterval = minimumPollInterval
            };
        }
    }
}
