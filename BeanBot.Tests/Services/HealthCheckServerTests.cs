using BeanBot.Services;
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
}
