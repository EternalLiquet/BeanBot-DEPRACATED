using System.Net;
using System.Net.Http.Headers;
using BeanBot.Configuration;
using BeanBot.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Health;

public class HealthCheckMetricsTests
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
    public async Task MetricsDisabled_ReturnsNotFoundWithoutChangingHealthRoutes()
    {
        await using var server = CreateServer(metricsEnabled: false);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var metrics = await client.GetAsync(HealthCheckServer.MetricsPath);
        using var health = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.NotFound, metrics.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
    }

    [Fact]
    public async Task MetricsEnabled_ReturnsPrometheusSnapshotWithoutReadinessWork()
    {
        var activeHealthCalls = 0;
        var mongoReadinessCalls = 0;
        var discordMetrics = new DiscordMetricsSnapshot(
            true,
            4,
            3,
            new DateTimeOffset(2026, 9, 17, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        var mongoMetrics = new MongoReadinessMetricsSnapshot(
            true,
            false,
            true,
            new DateTimeOffset(2026, 9, 17, 13, 5, 0, TimeSpan.Zero),
            8,
            2,
            1);

        await using var server = CreateServer(
            metricsEnabled: true,
            createHealthSnapshot: () =>
            {
                Interlocked.Increment(ref activeHealthCalls);
                return new DiscordHealthSnapshot(
                    false,
                    "secret-disconnect-reason",
                    "LoggedOut",
                    "Disconnected",
                    null,
                    null,
                    null,
                    "secret-disconnect-reason");
            },
            getMongoReadinessSnapshot: _ =>
            {
                Interlocked.Increment(ref mongoReadinessCalls);
                throw new InvalidOperationException("mongodb://user:password@secret-host");
            },
            createDiscordMetricsSnapshot: () => discordMetrics,
            createMongoMetricsSnapshot: () => mongoMetrics);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync(HealthCheckServer.MetricsPath);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("beanbot_discord_ready 1", body, StringComparison.Ordinal);
        Assert.Contains("beanbot_discord_ready_transitions_total 4", body, StringComparison.Ordinal);
        Assert.Contains("beanbot_discord_disconnect_transitions_total 3", body, StringComparison.Ordinal);
        Assert.Contains("beanbot_mongo_reachable 0", body, StringComparison.Ordinal);
        Assert.Contains("beanbot_mongo_state_known 1", body, StringComparison.Ordinal);
        Assert.Contains("beanbot_mongo_state_fresh 1", body, StringComparison.Ordinal);
        Assert.Contains("beanbot_mongo_probe_outcomes_total{result=\"success\"} 8", body, StringComparison.Ordinal);
        Assert.Contains("beanbot_mongo_probe_outcomes_total{result=\"failure\"} 2", body, StringComparison.Ordinal);
        Assert.Contains("beanbot_mongo_probe_outcomes_total{result=\"timeout\"} 1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-disconnect-reason", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mongodb://", body, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref activeHealthCalls));
        Assert.Equal(0, Volatile.Read(ref mongoReadinessCalls));
    }

    [Fact]
    public async Task MetricsEnabled_UnknownMongoStateIsExplicitAndDoesNotStartProbe()
    {
        var mongoReadinessCalls = 0;
        await using var server = CreateServer(
            metricsEnabled: true,
            getMongoReadinessSnapshot: _ =>
            {
                Interlocked.Increment(ref mongoReadinessCalls);
                return Task.FromResult(new MongoReadinessSnapshot(true, DateTimeOffset.UtcNow));
            });
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync(HealthCheckServer.MetricsPath);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("beanbot_mongo_state_known 0", body, StringComparison.Ordinal);
        Assert.Contains("beanbot_mongo_state_fresh 0", body, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref mongoReadinessCalls));
    }

    [Fact]
    public async Task MetricsEnabled_HeadReturnsGetHeadersWithoutBody()
    {
        await using var server = CreateServer(metricsEnabled: true);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var request = new HttpRequestMessage(HttpMethod.Head, HealthCheckServer.MetricsPath);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Content.Headers.ContentLength > 0);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MetricsEnabled_UsesBearerAuthenticationAndBoundedRateLimit()
    {
        await using var server = CreateServer(metricsEnabled: true, bearerToken: "metrics-secret");
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var missing = await client.GetAsync(HealthCheckServer.MetricsPath);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "metrics-secret");
        using var first = await client.GetAsync(HealthCheckServer.MetricsPath);
        using var second = await client.GetAsync(HealthCheckServer.MetricsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Contains("Bearer", missing.Headers.WwwAuthenticate.Select(value => value.Scheme));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task MetricsEnabled_StopAndDisposeRemainBoundedAndIdempotent()
    {
        var server = CreateServer(metricsEnabled: true);
        await server.StartAsync(CancellationToken.None);

        await server.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await server.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await server.DisposeAsync();

        Assert.Equal(0, server.BoundPort);
    }

    private static HealthCheckServer CreateServer(
        bool metricsEnabled,
        string? bearerToken = null,
        Func<DiscordHealthSnapshot>? createHealthSnapshot = null,
        Func<CancellationToken, Task<MongoReadinessSnapshot>>? getMongoReadinessSnapshot = null,
        Func<DiscordMetricsSnapshot>? createDiscordMetricsSnapshot = null,
        Func<MongoReadinessMetricsSnapshot>? createMongoMetricsSnapshot = null)
    {
        var options = new HealthCheckOptions(
            true,
            IPAddress.Loopback,
            0,
            bearerToken,
            TimeSpan.FromSeconds(30),
            metricsEnabled);
        return new HealthCheckServer(
            options,
            createHealthSnapshot ?? (() => UnhealthySnapshot),
            getMongoReadinessSnapshot ?? (_ => Task.FromResult(new MongoReadinessSnapshot(true, DateTimeOffset.UnixEpoch))),
            NullLogger<HealthCheckServer>.Instance,
            maximumConcurrentClients: 2,
            maximumTrackedRateLimitClients: 10,
            createDiscordMetricsSnapshot: createDiscordMetricsSnapshot,
            createMongoMetricsSnapshot: createMongoMetricsSnapshot);
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
}
