using System.Net;
using System.Text.Json;
using BeanBot.Configuration;
using BeanBot.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Health;

public class HealthCheckLifecycleReadinessTests
{
    private static readonly DiscordHealthSnapshot HealthyDiscordSnapshot = new(
        true,
        "BeanBot is connected to Discord.",
        "LoggedIn",
        "Connected",
        new DateTimeOffset(2026, 9, 23, 13, 0, 0, TimeSpan.Zero),
        null,
        null,
        null);

    [Fact]
    public async Task StartingLifecycle_Returns503WhileLivenessRemainsIndependent()
    {
        var readiness = new ApplicationReadinessState();
        await using var server = CreateServer(readiness);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var readinessResponse = await client.GetAsync("/healthz");
        using var payload = JsonDocument.Parse(await readinessResponse.Content.ReadAsStringAsync());
        using var livenessResponse = await client.GetAsync("/livez");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, readinessResponse.StatusCode);
        Assert.False(payload.RootElement.GetProperty("applicationReady").GetBoolean());
        Assert.Equal("starting", payload.RootElement.GetProperty("lifecycleState").GetString());
        Assert.Equal(
            "BeanBot startup is not complete yet.",
            payload.RootElement.GetProperty("message").GetString());
        Assert.True(payload.RootElement.GetProperty("discordConnected").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("mongoReachable").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, livenessResponse.StatusCode);
    }

    [Fact]
    public async Task ReadyLifecycle_WithHealthyDependencies_Returns200()
    {
        var readiness = new ApplicationReadinessState();
        readiness.MarkReady();
        await using var server = CreateServer(readiness);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/healthz");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(payload.RootElement.GetProperty("applicationReady").GetBoolean());
        Assert.Equal("ready", payload.RootElement.GetProperty("lifecycleState").GetString());
        Assert.True(payload.RootElement.GetProperty("discordConnected").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("mongoReachable").GetBoolean());
    }

    [Fact]
    public async Task DrainBeginningDuringMongoProbe_Returns503WithDrainingSnapshot()
    {
        var readiness = new ApplicationReadinessState();
        readiness.MarkReady();
        var mongoProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mongoProbeCompletion = new TaskCompletionSource<MongoReadinessSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(
            readiness,
            async cancellationToken =>
            {
                mongoProbeStarted.TrySetResult();
                return await mongoProbeCompletion.Task.WaitAsync(cancellationToken);
            });
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        var responseTask = client.GetAsync("/healthz");
        await mongoProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        readiness.BeginDraining();
        mongoProbeCompletion.TrySetResult(new MongoReadinessSnapshot(true, DateTimeOffset.UtcNow));

        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(1));
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(payload.RootElement.GetProperty("applicationReady").GetBoolean());
        Assert.Equal("draining", payload.RootElement.GetProperty("lifecycleState").GetString());
        Assert.Equal(
            "BeanBot is draining for shutdown.",
            payload.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task DrainingLifecycle_Returns503WhileDependenciesRemainHealthy()
    {
        var readiness = new ApplicationReadinessState();
        readiness.MarkReady();
        readiness.BeginDraining();
        await using var server = CreateServer(readiness);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/healthz");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(payload.RootElement.GetProperty("applicationReady").GetBoolean());
        Assert.Equal("draining", payload.RootElement.GetProperty("lifecycleState").GetString());
        Assert.Equal(
            "BeanBot is draining for shutdown.",
            payload.RootElement.GetProperty("message").GetString());
        Assert.True(payload.RootElement.GetProperty("discordConnected").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("mongoReachable").GetBoolean());
    }

    private static HealthCheckServer CreateServer(
        ApplicationReadinessState readiness,
        Func<CancellationToken, Task<MongoReadinessSnapshot>>? getMongoReadinessSnapshot = null)
    {
        var options = new HealthCheckOptions(
            true,
            IPAddress.Loopback,
            0,
            null,
            TimeSpan.FromMilliseconds(1));
        return new HealthCheckServer(
            options,
            () => HealthyDiscordSnapshot,
            getMongoReadinessSnapshot ??
                (_ => Task.FromResult(new MongoReadinessSnapshot(true, DateTimeOffset.UtcNow))),
            readiness.CreateSnapshot,
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
