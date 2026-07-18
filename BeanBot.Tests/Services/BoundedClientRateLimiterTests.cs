using BeanBot.Services;
using Xunit;

namespace BeanBot.Tests.Services;

public class BoundedClientRateLimiterTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    [Fact]
    public void NewClient_IsAllowedAndTracked()
    {
        var clock = new ManualClock();
        var limiter = new BoundedClientRateLimiter(PollInterval, 10, clock.GetUtcNow);

        Assert.False(limiter.IsRateLimited("client-a", out var retryAfter));
        Assert.Equal(0, retryAfter);
        Assert.Equal(1, limiter.TrackedClientCount);
    }

    [Fact]
    public void RequestWithinPollInterval_IsRateLimited()
    {
        var clock = new ManualClock();
        var limiter = new BoundedClientRateLimiter(PollInterval, 10, clock.GetUtcNow);
        limiter.IsRateLimited("client-a", out _);
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.True(limiter.IsRateLimited("client-a", out var retryAfter));
        Assert.Equal(6, retryAfter);
    }

    [Fact]
    public void RequestAfterPollInterval_IsAllowed()
    {
        var clock = new ManualClock();
        var limiter = new BoundedClientRateLimiter(PollInterval, 10, clock.GetUtcNow);
        limiter.IsRateLimited("client-a", out _);
        clock.Advance(PollInterval);

        Assert.False(limiter.IsRateLimited("client-a", out var retryAfter));
        Assert.Equal(0, retryAfter);
    }

    [Fact]
    public void Cleanup_RemovesStaleClientsAndPreservesRecentClients()
    {
        var clock = new ManualClock();
        var limiter = new BoundedClientRateLimiter(PollInterval, 10, clock.GetUtcNow);
        limiter.IsRateLimited("stale", out _);
        clock.Advance(TimeSpan.FromSeconds(11));
        limiter.IsRateLimited("recent", out _);
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.Equal(1, limiter.CleanupStaleEntries());
        Assert.Equal(1, limiter.TrackedClientCount);
        Assert.True(limiter.IsRateLimited("recent", out _));
    }

    [Fact]
    public void Capacity_IsNeverExceededAndActiveClientsAreNotEvicted()
    {
        var clock = new ManualClock();
        var limiter = new BoundedClientRateLimiter(PollInterval, 2, clock.GetUtcNow);
        Assert.False(limiter.IsRateLimited("client-a", out _));
        Assert.False(limiter.IsRateLimited("client-b", out _));

        Assert.True(limiter.IsRateLimited("client-c", out _));
        Assert.Equal(2, limiter.TrackedClientCount);
        Assert.True(limiter.IsRateLimited("client-a", out _));
    }

    [Fact]
    public void ExpiredCapacity_IsReusedByNewClient()
    {
        var clock = new ManualClock();
        var limiter = new BoundedClientRateLimiter(PollInterval, 1, clock.GetUtcNow);
        limiter.IsRateLimited("stale", out _);
        clock.Advance(PollInterval + PollInterval);

        Assert.False(limiter.IsRateLimited("replacement", out _));
        Assert.Equal(1, limiter.TrackedClientCount);
    }

    [Fact]
    public async Task ConcurrentCleanupAndRequests_RemainBoundedAndDoNotThrow()
    {
        var clock = new ManualClock();
        const int capacity = 32;
        var limiter = new BoundedClientRateLimiter(PollInterval, capacity, clock.GetUtcNow);

        var requestTasks = Enumerable.Range(0, 500)
            .Select(index => (Task)Task.Run(() => limiter.IsRateLimited($"client-{index}", out _)));
        var cleanupTasks = Enumerable.Range(0, 20)
            .Select(_ => (Task)Task.Run(() => limiter.CleanupStaleEntries()));

        await Task.WhenAll(requestTasks.Concat(cleanupTasks));
        Assert.InRange(limiter.TrackedClientCount, 0, capacity);
    }

    private sealed class ManualClock
    {
        private DateTimeOffset _utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
