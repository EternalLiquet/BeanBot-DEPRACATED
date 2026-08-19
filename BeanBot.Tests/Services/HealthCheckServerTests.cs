using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BeanBot.Configuration;
using BeanBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace BeanBot.Tests.Services;

[Collection("Serilog global logger")]
public class HealthCheckServerTests
{
    private static readonly DiscordHealthSnapshot UnhealthySnapshot = new(
        false,
        "Discord gateway has not reached the Ready state yet.",
        "LoggedOut",
        "Disconnected",
        null,
        null,
        null,
        null);

    [Fact]
    public async Task Get_UnhealthySnapshot_ReturnsExistingHealthContract()
    {
        await using var server = CreateServer(() => UnhealthySnapshot);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();
        using var payload = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unhealthy", payload.RootElement.GetProperty("status").GetString());
        Assert.False(payload.RootElement.GetProperty("discordConnected").GetBoolean());
        Assert.Equal("LoggedOut", payload.RootElement.GetProperty("loginState").GetString());
        Assert.Equal("Disconnected", payload.RootElement.GetProperty("connectionState").GetString());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("lastReadyAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("lastDisconnectedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("unhealthySinceAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("mostRecentDisconnectReason").ValueKind);
        Assert.False(payload.RootElement.TryGetProperty("lastDisconnectReason", out _));
    }

    [Fact]
    public async Task Get_HealthySnapshot_ReturnsOkAndHealthDetails()
    {
        var readyAt = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new DiscordHealthSnapshot(
            true,
            "BeanBot is connected to Discord.",
            "LoggedIn",
            "Connected",
            readyAt,
            null,
            null,
            null);
        await using var server = CreateServer(() => snapshot);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/healthz?probe=1");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", payload.RootElement.GetProperty("status").GetString());
        Assert.True(payload.RootElement.GetProperty("discordConnected").GetBoolean());
        Assert.Equal(readyAt, payload.RootElement.GetProperty("lastReadyAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Head_ReturnsGetContentLengthWithoutBody()
    {
        await using var server = CreateServer(() => UnhealthySnapshot);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);
        using var expectedGet = await client.GetAsync("/not-healthz");
        var expectedLength = expectedGet.Content.Headers.ContentLength;

        using var request = new HttpRequestMessage(HttpMethod.Head, "/not-healthz");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(expectedLength, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task EncodedHealthPath_ResolvesToConfiguredEndpoint()
    {
        await using var server = CreateServer(() => UnhealthySnapshot);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/health%7A");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task UnknownPathAndUnsupportedMethod_PreserveRoutingContract()
    {
        await using var server = CreateServer(() => UnhealthySnapshot);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var notFound = await client.GetAsync("/missing");
        using var methodNotAllowed = await client.PostAsync("/healthz", null);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, methodNotAllowed.StatusCode);
        Assert.Contains("GET", methodNotAllowed.Content.Headers.Allow);
        Assert.Contains("HEAD", methodNotAllowed.Content.Headers.Allow);
    }

    [Fact]
    public async Task BearerAuthentication_RejectsMissingAndInvalidTokensWithoutConsumingRateLimit()
    {
        await using var server = CreateServer(() => UnhealthySnapshot, bearerToken: "health-secret");
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var missing = await client.GetAsync("/healthz");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-secret");
        using var invalid = await client.GetAsync("/healthz");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "health-secret");
        using var valid = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Contains("Bearer", missing.Headers.WwwAuthenticate.Select(value => value.Scheme));
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, valid.StatusCode);
    }

    [Fact]
    public async Task RepeatedAuthorizedPoll_ReturnsBoundedRateLimitContract()
    {
        await using var server = CreateServer(() => UnhealthySnapshot, bearerToken: "health-secret");
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "health-secret");

        using var first = await client.GetAsync("/healthz");
        using var second = await client.GetAsync("/healthz");
        using var payload = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter?.Delta);
        Assert.Equal("rate_limited", payload.RootElement.GetProperty("status").GetString());
        Assert.True(payload.RootElement.GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task OversizedRequestLine_IsRejectedByKestrelAndServerRemainsAvailable()
    {
        await using var server = CreateServer(() => UnhealthySnapshot);
        await server.StartAsync(CancellationToken.None);
        var oversizedPath = "/" + new string('a', HealthCheckServer.MaxRequestLineLength);

        var response = await SendRawRequestAsync(
            server,
            $"GET {oversizedPath} HTTP/1.1\r\nHost: localhost\r\n\r\n");
        using var client = CreateClient(server);
        using var nextResponse = await client.GetAsync("/healthz");

        Assert.StartsWith("HTTP/1.1 414 URI Too Long", response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, nextResponse.StatusCode);
    }

    [Fact]
    public async Task OversizedHeaders_AreRejectedByKestrelAndServerRemainsAvailable()
    {
        await using var server = CreateServer(() => UnhealthySnapshot);
        await server.StartAsync(CancellationToken.None);
        var oversizedHeader = new string('a', HealthCheckServer.MaxHeaderCharacters);

        var response = await SendRawRequestAsync(
            server,
            $"GET /healthz HTTP/1.1\r\nHost: localhost\r\nX-Test: {oversizedHeader}\r\n\r\n");
        using var client = CreateClient(server);
        using var nextResponse = await client.GetAsync("/healthz");

        Assert.StartsWith("HTTP/1.1 431 Request Header Fields Too Large", response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, nextResponse.StatusCode);
    }

    [Fact]
    public async Task PartialHeaders_TimeOutAndServerRemainsAvailable()
    {
        await using var server = CreateServer(
            () => UnhealthySnapshot,
            requestHeadersTimeout: TimeSpan.FromMilliseconds(100));
        await server.StartAsync(CancellationToken.None);

        var response = await SendRawRequestAsync(server, "GET /healthz HTTP/1.1\r\nHost: localhost");
        using var client = CreateClient(server);
        using var nextResponse = await client.GetAsync("/healthz");

        Assert.StartsWith("HTTP/1.1 408 Request Timeout", response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, nextResponse.StatusCode);
    }

    [Fact]
    public async Task DisabledServer_DoesNotBindAListener()
    {
        var options = new HealthCheckOptions(
            false,
            IPAddress.Loopback,
            0,
            null,
            TimeSpan.FromSeconds(90));
        await using var server = new HealthCheckServer(
            options,
            () => UnhealthySnapshot,
            NullLogger<HealthCheckServer>.Instance);

        await server.StartAsync(CancellationToken.None);

        Assert.Equal(0, server.BoundPort);
    }

    [Fact]
    public async Task StartTwice_IsRejectedAndStopIsIdempotent()
    {
        await using var server = CreateServer(() => UnhealthySnapshot);
        await server.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.StartAsync(CancellationToken.None));
        await server.StopAsync(CancellationToken.None);
        await server.StopAsync(CancellationToken.None);

        Assert.Equal(0, server.BoundPort);
    }

    [Fact]
    public void SelectBoundPort_MultipleAddresses_ReturnsLowestValidPort()
    {
        var addresses = new[]
        {
            "http://127.0.0.1:54321",
            "not-an-address",
            "http://[::1]:12345",
            "http://127.0.0.1:23456"
        };

        var port = HealthCheckServer.SelectBoundPort(addresses);

        Assert.Equal(12345, port);
    }

    [Fact]
    public void SelectBoundPort_NoValidBoundAddress_ReturnsZero()
    {
        Assert.Equal(0, HealthCheckServer.SelectBoundPort(Array.Empty<string>()));
        Assert.Equal(0, HealthCheckServer.SelectBoundPort(new[] { "not-an-address" }));
    }

    [Fact]
    public async Task HandlerFailure_IsForwardedToExistingSerilogPipeline()
    {
        var previousLogger = Log.Logger;
        var sink = new CapturingLogSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        Log.Logger = logger;
        try
        {
            await using var server = CreateServer(
                () => throw new InvalidOperationException("health snapshot failed"));
            await server.StartAsync(CancellationToken.None);
            using var client = CreateClient(server);

            try
            {
                using var response = await client.GetAsync("/healthz");
            }
            catch (HttpRequestException)
            {
                // Kestrel may abort a response whose handler failed before headers.
            }

            await WaitUntilAsync(() => sink.Events.Any(logEvent =>
                logEvent.Level >= LogEventLevel.Error &&
                logEvent.Exception?.Message == "health snapshot failed"));
            Assert.DoesNotContain(
                sink.Events,
                logEvent =>
                    logEvent.Properties.ContainsKey("SourceContext") &&
                    logEvent.Level < LogEventLevel.Warning);
        }
        finally
        {
            Log.Logger = previousLogger;
            logger.Dispose();
        }
    }

    [Fact]
    public async Task Shutdown_CancelsIncompleteRequestWithinDeadline()
    {
        var server = CreateServer(
            () => UnhealthySnapshot,
            requestHeadersTimeout: TimeSpan.FromSeconds(30));
        await server.StartAsync(CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /healthz HTTP/1.1"));

        await server.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await server.DisposeAsync();

        Assert.Equal(0, server.BoundPort);
    }

    private static HealthCheckServer CreateServer(
        Func<DiscordHealthSnapshot> createSnapshot,
        string? bearerToken = null,
        TimeSpan? requestHeadersTimeout = null)
    {
        var options = new HealthCheckOptions(
            true,
            IPAddress.Loopback,
            0,
            bearerToken,
            TimeSpan.FromSeconds(30));
        return new HealthCheckServer(
            options,
            createSnapshot,
            NullLogger<HealthCheckServer>.Instance,
            requestHeadersTimeout,
            maximumConcurrentClients: 2,
            maximumTrackedRateLimitClients: 10);
    }

    private static HttpClient CreateClient(HealthCheckServer server)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}")
        };
    }

    private static async Task<string> SendRawRequestAsync(HealthCheckServer server, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        await stream.FlushAsync();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < timeoutAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(condition(), "Condition was not reached before the test timeout.");
    }

    private sealed class CapturingLogSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}

[CollectionDefinition("Serilog global logger", DisableParallelization = true)]
public sealed class SerilogGlobalLoggerCollection
{
}
