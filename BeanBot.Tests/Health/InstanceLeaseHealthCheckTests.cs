using System.Net;
using System.Text.Json;
using BeanBot.Configuration;
using BeanBot.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Health;

public class InstanceLeaseHealthCheckTests
{
    private static readonly DiscordHealthSnapshot HealthyDiscord = new(
        true,
        "BeanBot is connected to Discord.",
        "LoggedIn",
        "Connected",
        DateTimeOffset.UtcNow,
        null,
        null,
        null);

    [Fact]
    public async Task Readiness_HealthyDependenciesWithoutLeaseReturnsServiceUnavailable()
    {
        await using var server = CreateServer(instanceLeaseHeld: false);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/healthz");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("unhealthy", payload.RootElement.GetProperty("status").GetString());
        Assert.True(payload.RootElement.GetProperty("discordConnected").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("mongoReachable").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("instanceLeaseHeld").GetBoolean());
        Assert.Equal(
            "Active instance ownership is not held.",
            payload.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Readiness_HeldLeaseWithHealthyDependenciesReturnsOk()
    {
        await using var server = CreateServer(instanceLeaseHeld: true);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/healthz");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(payload.RootElement.GetProperty("instanceLeaseHeld").GetBoolean());
    }

    [Fact]
    public async Task Liveness_DoesNotDependOnLeaseOwnership()
    {
        await using var server = CreateServer(instanceLeaseHeld: false);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync(HealthCheckServer.LivenessPath);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("alive", payload.RootElement.GetProperty("status").GetString());
        Assert.False(payload.RootElement.TryGetProperty("instanceLeaseHeld", out _));
    }

    private static HealthCheckServer CreateServer(bool instanceLeaseHeld)
    {
        var options = new HealthCheckOptions(
            true,
            IPAddress.Loopback,
            0,
            null,
            TimeSpan.FromMilliseconds(1));
        return new HealthCheckServer(
            options,
            () => HealthyDiscord,
            static cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new MongoReadinessSnapshot(true, DateTimeOffset.UtcNow));
            },
            NullLogger<HealthCheckServer>.Instance,
            maximumConcurrentClients: 2,
            maximumTrackedRateLimitClients: 10,
            isInstanceLeaseHeld: () => instanceLeaseHeld);
    }

    private static HttpClient CreateClient(HealthCheckServer server)
        => new(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}")
        };
}
