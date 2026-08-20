using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeanBot.Configuration;
using BeanBot.Hosting;
using BeanBot.Logging;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace BeanBot.Health;

public sealed class HealthCheckServer : IAsyncDisposable
{
    internal const int MaxRequestLineLength = 2048;
    internal const int MaxHeaderCount = 100;
    internal const int MaxHeaderCharacters = 32 * 1024;
    internal const int DefaultMaximumConcurrentClients = 64;
    internal const int DefaultMaximumTrackedRateLimitClients = 4096;
    private static readonly TimeSpan DefaultRequestHeadersTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _syncRoot = new();
    private readonly HealthCheckOptions _options;
    private readonly Func<DiscordHealthSnapshot> _createHealthSnapshot;
    private readonly TimeSpan _requestHeadersTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly int _maximumConcurrentClients;
    private readonly BoundedClientRateLimiter _rateLimiter;
    private readonly ILogger<HealthCheckServer> _logger;
    private WebApplication? _application;
    private int _boundPort;
    private int _disposed;

    public HealthCheckServer(
        HealthCheckOptions options,
        DiscordSocketClient discordClient,
        DiscordConnectionHealth discordConnectionHealth,
        ILogger<HealthCheckServer> logger)
        : this(
            options,
            CreateSnapshotFactory(discordClient, discordConnectionHealth),
            logger,
            DefaultRequestHeadersTimeout,
            DefaultMaximumConcurrentClients,
            DefaultMaximumTrackedRateLimitClients,
            DefaultShutdownTimeout)
    {
    }

    internal HealthCheckServer(
        HealthCheckOptions options,
        Func<DiscordHealthSnapshot> createHealthSnapshot,
        ILogger<HealthCheckServer> logger,
        TimeSpan? requestHeadersTimeout = null,
        int maximumConcurrentClients = DefaultMaximumConcurrentClients,
        int maximumTrackedRateLimitClients = DefaultMaximumTrackedRateLimitClients,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createHealthSnapshot);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrentClients);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTrackedRateLimitClients);

        var effectiveRequestHeadersTimeout = requestHeadersTimeout ?? DefaultRequestHeadersTimeout;
        var effectiveShutdownTimeout = shutdownTimeout ?? DefaultShutdownTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveRequestHeadersTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveShutdownTimeout, TimeSpan.Zero);

        _options = options;
        _createHealthSnapshot = createHealthSnapshot;
        _logger = logger;
        _requestHeadersTimeout = effectiveRequestHeadersTimeout;
        _shutdownTimeout = effectiveShutdownTimeout;
        _maximumConcurrentClients = maximumConcurrentClients;
        _rateLimiter = new BoundedClientRateLimiter(
            options.MinimumPollInterval,
            maximumTrackedRateLimitClients);
    }

    internal int BoundPort
    {
        get
        {
            lock (_syncRoot)
            {
                return _boundPort;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        WebApplication application;
        lock (_syncRoot)
        {
            if (_application is not null)
            {
                throw new InvalidOperationException("The health check server has already been started.");
            }

            application = CreateApplication();
            _application = application;
        }

        try
        {
            await application.StartAsync(cancellationToken);
        }
        catch
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_application, application))
                {
                    _application = null;
                    _boundPort = 0;
                }
            }

            await application.DisposeAsync();
            throw;
        }

        lock (_syncRoot)
        {
            if (ReferenceEquals(_application, application))
            {
                _boundPort = FindBoundPort(application);
            }
        }

        BeanBotLog.HealthEndpointListening(
            _logger,
            _options.BindAddress,
            BoundPort,
            _options.Path,
            (int)_options.MinimumPollInterval.TotalSeconds);

        if (string.IsNullOrWhiteSpace(_options.BearerToken)
            && !_options.BindAddress.Equals(IPAddress.Loopback)
            && !_options.BindAddress.Equals(IPAddress.IPv6Loopback))
        {
            BeanBotLog.HealthEndpointUnauthenticated(
                _logger,
                _options.BindAddress,
                BoundPort,
                _options.Path);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        WebApplication? application;
        lock (_syncRoot)
        {
            application = _application;
            _application = null;
            _boundPort = 0;
        }

        if (application is null)
        {
            return;
        }

        try
        {
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdown.CancelAfter(_shutdownTimeout);
            try
            {
                await application.StopAsync(shutdown.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                BeanBotLog.HealthEndpointShutdownTimedOut(
                    _logger,
                    _shutdownTimeout.TotalSeconds);
            }
        }
        finally
        {
            await application.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None);
    }

    private static Func<DiscordHealthSnapshot> CreateSnapshotFactory(
        DiscordSocketClient discordClient,
        DiscordConnectionHealth discordConnectionHealth)
    {
        ArgumentNullException.ThrowIfNull(discordClient);
        ArgumentNullException.ThrowIfNull(discordConnectionHealth);
        return () => discordConnectionHealth.CreateSnapshot(discordClient);
    }

    private WebApplication CreateApplication()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(HealthCheckServer).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);
        builder.Logging.AddFilter<SerilogLoggerProvider>(
            (_, logLevel) => logLevel >= LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Listen(_options.BindAddress, _options.Port);
            serverOptions.Limits.MaxConcurrentConnections = _maximumConcurrentClients;
            serverOptions.Limits.MaxRequestLineSize = MaxRequestLineLength;
            serverOptions.Limits.MaxRequestHeaderCount = MaxHeaderCount;
            serverOptions.Limits.MaxRequestHeadersTotalSize = MaxHeaderCharacters;
            serverOptions.Limits.RequestHeadersTimeout = _requestHeadersTimeout;
        });

        var application = builder.Build();
        application.Run(HandleRequestAsync);
        return application;
    }

    private static int FindBoundPort(WebApplication application)
    {
        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        return SelectBoundPort(addresses ?? Array.Empty<string>());
    }

    internal static int SelectBoundPort(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        // Kestrel normally exposes one address for this server. If hosting
        // configuration adds more, the lowest actual TCP port is stable and
        // avoids making lifecycle logging or test discovery order-dependent.
        return addresses
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Port : 0)
            .Where(port => port > 0)
            .DefaultIfEmpty(0)
            .Min();
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        context.Response.Headers.Connection = "close";
        var isHeadRequest = HttpMethods.IsHead(context.Request.Method);
        if (!HttpMethods.IsGet(context.Request.Method) && !isHeadRequest)
        {
            context.Response.Headers.Allow = "GET, HEAD";
            await WritePlainTextResponseAsync(
                context,
                StatusCodes.Status405MethodNotAllowed,
                "Only GET and HEAD are supported.",
                suppressBody: false);
            return;
        }

        if (!string.Equals(context.Request.Path.Value, _options.Path, StringComparison.OrdinalIgnoreCase))
        {
            await WritePlainTextResponseAsync(
                context,
                StatusCodes.Status404NotFound,
                "Not Found",
                isHeadRequest);
            return;
        }

        if (!IsAuthorized(context.Request))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await WritePlainTextResponseAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "Missing or invalid bearer token.",
                isHeadRequest);
            return;
        }

        var clientIdentifier = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_rateLimiter.IsRateLimited(clientIdentifier, out var retryAfterSeconds))
        {
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await WriteJsonResponseAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                new
                {
                    status = "rate_limited",
                    message = $"Wait {retryAfterSeconds} more seconds before polling {_options.Path} again.",
                    retryAfterSeconds
                },
                isHeadRequest);
            return;
        }

        var healthSnapshot = _createHealthSnapshot();
        await WriteJsonResponseAsync(
            context,
            healthSnapshot.IsHealthy
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable,
            new
            {
                status = healthSnapshot.IsHealthy ? "ok" : "unhealthy",
                version = BuildIdentity.Current.Version,
                commitSha = BuildIdentity.Current.CommitSha,
                discordConnected = healthSnapshot.IsHealthy,
                message = healthSnapshot.StatusMessage,
                loginState = healthSnapshot.LoginState,
                connectionState = healthSnapshot.ConnectionState,
                lastReadyAtUtc = healthSnapshot.LastReadyAtUtc,
                lastDisconnectedAtUtc = healthSnapshot.LastDisconnectedAtUtc,
                unhealthySinceAtUtc = healthSnapshot.UnhealthySinceAtUtc,
                mostRecentDisconnectReason = healthSnapshot.MostRecentDisconnectReason
            },
            isHeadRequest);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
        {
            return true;
        }

        var authorizationHeader = request.Headers.Authorization.ToString();
        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providedToken = Encoding.UTF8.GetBytes(authorizationHeader["Bearer ".Length..].Trim());
        var expectedToken = Encoding.UTF8.GetBytes(_options.BearerToken);
        return CryptographicOperations.FixedTimeEquals(providedToken, expectedToken);
    }

    private static Task WritePlainTextResponseAsync(
        HttpContext context,
        int statusCode,
        string body,
        bool suppressBody)
    {
        return WriteResponseAsync(
            context,
            statusCode,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(body),
            suppressBody);
    }

    private static Task WriteJsonResponseAsync(
        HttpContext context,
        int statusCode,
        object payload,
        bool suppressBody)
    {
        return WriteResponseAsync(
            context,
            statusCode,
            "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
            suppressBody);
    }

    private static async Task WriteResponseAsync(
        HttpContext context,
        int statusCode,
        string contentType,
        byte[] body,
        bool suppressBody)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = body.Length;
        if (!suppressBody)
        {
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
    }
}
