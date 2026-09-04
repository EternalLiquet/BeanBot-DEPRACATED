using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BeanBot.Configuration;
using BeanBot.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Health;

public class HealthCheckLivenessTests
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
    public async Task Get_Liveness_ReturnsMinimalPayloadWithoutCreatingReadinessSnapshot()
    {
        await using var server = CreateServer(
            () => throw new InvalidOperationException("Readiness must not run for liveness."));
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.GetAsync("/livez?probe=1");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("alive", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("0.0.0-local", payload.RootElement.GetProperty("version").GetString());
        Assert.Equal("unknown", payload.RootElement.GetProperty("commitSha").GetString());
        Assert.Equal(3, payload.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Head_Liveness_ReturnsHeadersWithoutBodyOrReadinessWork()
    {
        await using var server = CreateServer(
            () => throw new InvalidOperationException("Readiness must not run for liveness."));
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/livez");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Content.Headers.ContentLength > 0);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task DiscordUnready_ReadinessIs503WhileLivenessRemains200()
    {
        var snapshotCalls = 0;
        await using var server = CreateServer(() =>
        {
            Interlocked.Increment(ref snapshotCalls);
            return UnhealthySnapshot;
        });
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var liveness = await client.GetAsync("/livez");
        Assert.Equal(0, Volatile.Read(ref snapshotCalls));
        using var readiness = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal(1, Volatile.Read(ref snapshotCalls));
    }

    [Fact]
    public async Task BearerAuthentication_AppliesToLivenessWithoutFailuresConsumingRateLimit()
    {
        await using var server = CreateServer(
            () => UnhealthySnapshot,
            bearerToken: "health-secret");
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var missing = await client.GetAsync("/livez");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-secret");
        using var invalid = await client.GetAsync("/livez");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "health-secret");
        using var valid = await client.GetAsync("/livez");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Contains("Bearer", missing.Headers.WwwAuthenticate.Select(value => value.Scheme));
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task UnsupportedMethod_LivenessUsesExistingSafe405Contract()
    {
        await using var server = CreateServer(() => UnhealthySnapshot);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var response = await client.PostAsync("/livez", null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("GET", response.Content.Headers.Allow);
        Assert.Contains("HEAD", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task ReadinessAndLiveness_ShareOneBoundedRateLimitStore()
    {
        var snapshotCalls = 0;
        await using var server = CreateServer(
            () =>
            {
                Interlocked.Increment(ref snapshotCalls);
                return UnhealthySnapshot;
            },
            maximumTrackedRateLimitClients: 1);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var readiness = await client.GetAsync("/healthz");
        using var liveness = await client.GetAsync("/livez");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, liveness.StatusCode);
        Assert.NotNull(liveness.Headers.RetryAfter?.Delta);
        Assert.Equal(1, Volatile.Read(ref snapshotCalls));
    }

    [Fact]
    public async Task StopAfterServingBothRoutes_RemainsIdempotent()
    {
        var server = CreateServer(
            () => UnhealthySnapshot,
            maximumTrackedRateLimitClients: 2);
        await server.StartAsync(CancellationToken.None);
        using var client = CreateClient(server);

        using var liveness = await client.GetAsync("/livez");
        using var readiness = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);

        await server.StopAsync(CancellationToken.None);
        await server.StopAsync(CancellationToken.None);
        await server.DisposeAsync();

        Assert.Equal(0, server.BoundPort);
    }

    private static HealthCheckServer CreateServer(
        Func<DiscordHealthSnapshot> createSnapshot,
        string? bearerToken = null,
        int maximumTrackedRateLimitClients = 10)
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
            maximumConcurrentClients: 2,
            maximumTrackedRateLimitClients: maximumTrackedRateLimitClients);
    }

    private static HttpClient CreateClient(HealthCheckServer server)
        => new(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}")
        };
}
