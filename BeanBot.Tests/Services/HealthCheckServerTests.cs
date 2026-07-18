using BeanBot.Configuration;
using BeanBot.Services;
using BeanBot.Util;
using Discord.WebSocket;
using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace BeanBot.Tests.Services;

[Collection("Serilog global logger")]
public class HealthCheckServerTests
{
    [Theory]
    [InlineData("GET /healthz HTTP/1.1", "GET", "/healthz")]
    [InlineData("HEAD /healthz?probe=1 HTTP/1.0", "HEAD", "/healthz?probe=1")]
    public void TryParseRequestLine_AcceptsSupportedHttpVersions(string request, string expectedMethod, string expectedTarget)
    {
        Assert.True(HealthCheckServer.TryParseRequestLine(request, out var method, out var target));
        Assert.Equal(expectedMethod, method);
        Assert.Equal(expectedTarget, target);
    }

    [Theory]
    [InlineData("GET /healthz")]
    [InlineData("GET /healthz HTTP/2")]
    [InlineData("GET /healthz HTTP/1.1 extra")]
    public void TryParseRequestLine_RejectsMalformedRequests(string request)
    {
        Assert.False(HealthCheckServer.TryParseRequestLine(request, out _, out _));
    }

    [Theory]
    [InlineData("/healthz", "/healthz")]
    [InlineData("/healthz?probe=1", "/healthz")]
    [InlineData("/health%7A", "/healthz")]
    [InlineData("/health%7A/%41%42", "/healthz/AB")]
    [InlineData("", "/")]
    public void TryExtractPath_RemovesQueryAndDecodesValidPath(string target, string expected)
    {
        Assert.True(HealthCheckServer.TryExtractPath(target, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("/%")]
    [InlineData("/%7")]
    [InlineData("/%ZZ")]
    [InlineData("/%41/%ZZ")]
    [InlineData("/%ZZ/%41")]
    public void TryExtractPath_RejectsMalformedPercentEscapes(string target)
    {
        Assert.False(HealthCheckServer.TryExtractPath(target, out var path));
        Assert.Null(path);
    }

    [Fact]
    public async Task RequestLine_ExactlyAtLimit_IsAccepted()
    {
        const string prefix = "GET /";
        const string suffix = " HTTP/1.1";
        var padding = new string('a', HealthCheckServer.MaxRequestLineLength - prefix.Length - suffix.Length);

        var response = await SendRequestAsync($"{prefix}{padding}{suffix}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 404 Not Found", response);
    }

    [Fact]
    public async Task RequestLine_OneCharacterOverLimit_Returns414()
    {
        const string prefix = "GET /";
        const string suffix = " HTTP/1.1";
        var padding = new string('a', HealthCheckServer.MaxRequestLineLength - prefix.Length - suffix.Length + 1);

        var response = await SendRequestAsync($"{prefix}{padding}{suffix}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 414 URI Too Long", response);
    }

    [Fact]
    public async Task SingleOversizedHeader_Returns431()
    {
        var oversizedHeader = "X-Test: " + new string('a', HealthCheckServer.MaxHeaderLineLength);

        var response = await SendRequestAsync($"GET /healthz HTTP/1.1\r\n{oversizedHeader}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 431 Request Header Fields Too Large", response);
    }

    [Fact]
    public async Task HeaderLine_ExactlyAtLimit_IsAccepted()
    {
        const string prefix = "X-Test: ";
        var header = prefix + new string('a', HealthCheckServer.MaxHeaderLineLength - prefix.Length);

        var response = await SendRequestAsync($"GET /healthz HTTP/1.1\r\n{header}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 503 Service Unavailable", response);
    }

    [Fact]
    public async Task HeadersExceedingTotalCharacterLimit_Return431()
    {
        var headers = string.Join("\r\n", Enumerable.Range(0, 5)
            .Select(index => $"X-{index}: {new string('a', 7000)}"));

        var response = await SendRequestAsync($"GET /healthz HTTP/1.1\r\n{headers}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 431 Request Header Fields Too Large", response);
    }

    [Fact]
    public async Task TooManyHeaders_Returns431()
    {
        var headers = string.Join("\r\n", Enumerable.Range(0, HealthCheckServer.MaxHeaderCount + 1)
            .Select(index => $"X-{index}: value"));

        var response = await SendRequestAsync($"GET /healthz HTTP/1.1\r\n{headers}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 431 Request Header Fields Too Large", response);
    }

    [Fact]
    public async Task PartialRequestWithoutLineTerminator_Returns408AfterTimeout()
    {
        var response = await SendRequestAsync(
            "GET /healthz HTTP/1.1",
            requestTimeout: TimeSpan.FromMilliseconds(100));

        Assert.StartsWith("HTTP/1.1 408 Request Timeout", response);
    }

    [Fact]
    public async Task ValidRequest_ReturnsHealthPayload()
    {
        var response = await SendRequestAsync("GET /healthz HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 503 Service Unavailable", response);
        Assert.Contains("\"status\":\"unhealthy\"", response);
    }

    [Fact]
    public async Task ValidEncodedHealthTarget_ResolvesToConfiguredPath()
    {
        var response = await SendRequestAsync("GET /health%7A HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 503 Service Unavailable", response);
        Assert.Contains("\"status\":\"unhealthy\"", response);
    }

    [Theory]
    [InlineData("/%")]
    [InlineData("/%7")]
    [InlineData("/%ZZ")]
    [InlineData("/%41/%ZZ")]
    public async Task MalformedTarget_Returns400WithoutOwnerNotification(string target)
    {
        var previousLogger = Log.Logger;
        var notifier = new CapturingOwnerNotifier();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new DiscordOwnerErrorSink(notifier))
            .CreateLogger();
        Log.Logger = logger;
        try
        {
            var response = await SendRequestAsync($"GET {target} HTTP/1.1\r\nHost: localhost\r\n\r\n");

            Assert.StartsWith("HTTP/1.1 400 Bad Request", response);
            Assert.Empty(notifier.Alerts);
        }
        finally
        {
            Log.Logger = previousLogger;
            logger.Dispose();
        }
    }

    [Fact]
    public async Task HeadRequest_PreservesGetContentLengthWithoutSendingBody()
    {
        var response = await SendRequestAsync("HEAD /healthz HTTP/1.1\r\nHost: localhost\r\n\r\n");
        var parts = response.Split("\r\n\r\n", 2);

        Assert.StartsWith("HTTP/1.1 503 Service Unavailable", response);
        Assert.Contains("Content-Length:", parts[0]);
        Assert.Equal(string.Empty, parts[1]);
    }

    [Fact]
    public async Task ConnectionBurst_NeverExceedsCapacityAndRejectsExcessClient()
    {
        using var discordClient = new DiscordSocketClient();
        await using var server = CreateServer(discordClient, TimeSpan.FromSeconds(5), maximumConcurrentClients: 2);
        server.Start();
        using var first = await ConnectAsync(server);
        using var second = await ConnectAsync(server);
        await WaitUntilAsync(() => server.ActiveClientHandlerCount == 2);

        using var excess = await ConnectAsync(server);

        Assert.True(await IsConnectionClosedAsync(excess));
        Assert.Equal(2, server.PeakActiveClientHandlers);
        Assert.InRange(server.ActiveClientTaskCount, 0, 2);
    }

    [Fact]
    public async Task CompletedRequest_ReleasesClientCapacity()
    {
        using var discordClient = new DiscordSocketClient();
        await using var server = CreateServer(discordClient, TimeSpan.FromSeconds(2), maximumConcurrentClients: 1);
        server.Start();

        var firstResponse = await SendRequestToServerAsync(server, "GET /healthz HTTP/1.1\r\n\r\n");
        var secondResponse = await SendRequestToServerAsync(server, "GET /healthz HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 503 Service Unavailable", firstResponse);
        Assert.StartsWith("HTTP/1.1 429 Too Many Requests", secondResponse);
        Assert.Equal(1, server.PeakActiveClientHandlers);
    }

    [Fact]
    public async Task DisconnectedRequest_ReleasesClientCapacity()
    {
        using var discordClient = new DiscordSocketClient();
        await using var server = CreateServer(discordClient, TimeSpan.FromSeconds(2), maximumConcurrentClients: 1);
        server.Start();
        var disconnectedClient = await ConnectAsync(server);
        await WaitUntilAsync(() => server.ActiveClientHandlerCount == 1);

        disconnectedClient.Dispose();
        await WaitUntilAsync(() => server.ActiveClientHandlerCount == 0);
        var response = await SendRequestToServerAsync(server, "GET /healthz HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 503 Service Unavailable", response);
    }

    [Fact]
    public async Task TimedOutRequest_ReleasesClientCapacity()
    {
        using var discordClient = new DiscordSocketClient();
        await using var server = CreateServer(discordClient, TimeSpan.FromMilliseconds(100), maximumConcurrentClients: 1);
        server.Start();
        using var timedOutClient = await ConnectAsync(server);
        await using var timedOutStream = timedOutClient.GetStream();
        await timedOutStream.WriteAsync(Encoding.ASCII.GetBytes("GET /healthz HTTP/1.1"));
        await timedOutStream.FlushAsync();
        using var timedOutReader = new StreamReader(timedOutStream, Encoding.ASCII, leaveOpen: true);

        var timeoutResponse = await timedOutReader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => server.ActiveClientHandlerCount == 0);
        var nextResponse = await SendRequestToServerAsync(server, "GET /healthz HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 408 Request Timeout", timeoutResponse);
        Assert.StartsWith("HTTP/1.1 503 Service Unavailable", nextResponse);
    }

    [Fact]
    public async Task Shutdown_WithCapacityExhausted_CancelsHandlersAndEmptiesTaskCollection()
    {
        using var discordClient = new DiscordSocketClient();
        var server = CreateServer(discordClient, TimeSpan.FromSeconds(30), maximumConcurrentClients: 1);
        server.Start();
        using var activeClient = await ConnectAsync(server);
        await WaitUntilAsync(() => server.ActiveClientHandlerCount == 1);
        using var excessClient = await ConnectAsync(server);
        Assert.True(await IsConnectionClosedAsync(excessClient));

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, server.ActiveClientHandlerCount);
        Assert.Equal(0, server.ActiveClientTaskCount);
    }

    private static async Task<string> SendRequestAsync(
        string request,
        TimeSpan? requestTimeout = null)
    {
        var options = new HealthCheckOptions(
            true,
            IPAddress.Loopback,
            0,
            null,
            TimeSpan.FromSeconds(1));
        using var discordClient = new DiscordSocketClient();
        await using var server = new HealthCheckServer(
            options,
            discordClient,
            new DiscordConnectionHealth(),
            requestTimeout ?? TimeSpan.FromSeconds(2));
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        await using var stream = client.GetStream();
        var requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static HealthCheckServer CreateServer(
        DiscordSocketClient discordClient,
        TimeSpan requestTimeout,
        int maximumConcurrentClients)
    {
        var options = new HealthCheckOptions(
            true,
            IPAddress.Loopback,
            0,
            null,
            TimeSpan.FromSeconds(1));
        return new HealthCheckServer(
            options,
            discordClient,
            new DiscordConnectionHealth(),
            requestTimeout,
            maximumConcurrentClients);
    }

    private static async Task<TcpClient> ConnectAsync(HealthCheckServer server)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        return client;
    }

    private static async Task<string> SendRequestToServerAsync(HealthCheckServer server, string request)
    {
        using var client = await ConnectAsync(server);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        await stream.FlushAsync();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task<bool> IsConnectionClosedAsync(TcpClient client)
    {
        try
        {
            var buffer = new byte[1];
            return await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0;
        }
        catch (Exception exception) when (exception is IOException || exception is SocketException)
        {
            return true;
        }
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

    private sealed class CapturingOwnerNotifier : IOwnerErrorNotifier
    {
        public List<string> Alerts { get; } = new List<string>();

        public void Enqueue(string alert) => Alerts.Add(alert);
    }
}

[CollectionDefinition("Serilog global logger", DisableParallelization = true)]
public sealed class SerilogGlobalLoggerCollection
{
}
