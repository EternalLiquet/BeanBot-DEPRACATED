using System.Net;
using System.Text.Json;
using BeanBot.Configuration;
using BeanBot.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Health;

public class HealthCheckMongoReadinessTests
{
    private static readonly DiscordHealthSnapshot HealthyDiscordSnapshot = new(
        true,
        "BeanBot is connected to Discord.",
        "LoggedIn",
        "Connected",
        new DateTimeOffset(2026, 9, 3, 13, 0, 0, TimeSpan.Zero),
        null,
        null,
        null);

    [Fact]
    public async Task DiscordAndMongoReady_ReturnsOkWithSanitizedMongoMetadata()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 3, 13, 5, 0, TimeSpan.Zero);
        await using var server = CreateServer(
            () => HealthyDiscordSnapshot,
            _ => Task.FromResult(new MongoReadinessSnapshot(true, checkedAt)));
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/healthz");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", payload.RootElement.GetProperty("status").GetString());
        Assert.True(payload.RootElement.GetProperty("discordConnected").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("mongoReachable").GetBoolean());
        Assert.Equal(
            checkedAt,
            payload.RootElement.GetProperty("mongoLastCheckedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task DiscordReadyButMongoUnavailable_Returns503WithoutFalsifyingDiscordStateOrLeakingDetails()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 3, 13, 5, 0, TimeSpan.Zero);
        await using var server = CreateServer(
            () => HealthyDiscordSnapshot,
            _ => Task.FromResult(new MongoReadinessSnapshot(false, checkedAt)));
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();
        using var payload = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("unhealthy", payload.RootElement.GetProperty("status").GetString());
        Assert.True(payload.RootElement.GetProperty("discordConnected").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("mongoReachable").GetBoolean());
        Assert.Equal(
            checkedAt,
            payload.RootElement.GetProperty("mongoLastCheckedAtUtc").GetDateTimeOffset());
        Assert.Equal("MongoDB is not reachable.", payload.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("mongodb://", body);
        Assert.DoesNotContain("password", body);
        Assert.DoesNotContain("connection string", body);
    }

    [Fact]
    public async Task Head_PreservesCombinedReadinessStatusWithoutBody()
    {
        await using var server = CreateServer(
            () => HealthyDiscordSnapshot,
            _ => Task.FromResult(new MongoReadinessSnapshot(false, DateTimeOffset.UtcNow)));
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/healthz");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private static HealthCheckServer CreateServer(
        Func<DiscordHealthSnapshot> createDiscordSnapshot,
        Func<CancellationToken, Task<MongoReadinessSnapshot>> getMongoSnapshot)
    {
        var options = new HealthCheckOptions(
            true,
            IPAddress.Loopback,
            0,
            null,
            TimeSpan.FromMilliseconds(1));
        return new HealthCheckServer(
            options,
            createDiscordSnapshot,
            getMongoSnapshot,
            NullLogger<HealthCheckServer>.Instance,
            maximumConcurrentClients: 2,
            maximumTrackedRateLimitClients: 10);
    }

    private static HttpClient CreateClient(HealthCheckServer server)
        => new(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}")
        };
}
