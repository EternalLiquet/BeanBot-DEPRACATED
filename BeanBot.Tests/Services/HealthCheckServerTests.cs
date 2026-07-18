using BeanBot.Configuration;
using BeanBot.Services;
using Discord.WebSocket;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace BeanBot.Tests.Services;

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
    [InlineData("/healthz?probe=1", "/healthz")]
    [InlineData("/health%7A", "/healthz")]
    [InlineData("", "/")]
    public void ExtractPath_RemovesQueryAndDecodesPath(string target, string expected)
    {
        Assert.Equal(expected, HealthCheckServer.ExtractPath(target));
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
    public async Task HeadRequest_PreservesGetContentLengthWithoutSendingBody()
    {
        var response = await SendRequestAsync("HEAD /healthz HTTP/1.1\r\nHost: localhost\r\n\r\n");
        var parts = response.Split("\r\n\r\n", 2);

        Assert.StartsWith("HTTP/1.1 503 Service Unavailable", response);
        Assert.Contains("Content-Length:", parts[0]);
        Assert.Equal(string.Empty, parts[1]);
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
}
