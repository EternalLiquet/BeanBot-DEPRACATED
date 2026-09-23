using BeanBot.Hosting;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotInstanceLeaseCleanupTests
{
    private static readonly DateTime StartUtc = new(2026, 9, 21, 14, 0, 0, DateTimeKind.Utc);
    private static readonly InstanceLeaseOptions TestOptions = new(
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromSeconds(1));

    [Fact]
    public async Task AcquireAsync_RepeatedSameIdentityIsIdempotent()
    {
        var store = new CleanupLeaseStore();
        await using var lease = CreateLease(store);

        await lease.AcquireAsync(42, CancellationToken.None);
        await lease.AcquireAsync(42, CancellationToken.None);

        Assert.True(lease.IsHeld);
        Assert.Equal(1, store.AcquireCount);
        await lease.ReleaseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReleaseAsync_UnresponsiveDeleteIsBoundedAndLateFaultIsObserved()
    {
        var blockedRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CleanupLeaseStore
        {
            Release = (_, _, _) => blockedRelease.Task
        };
        await using var lease = CreateLease(store);
        await lease.AcquireAsync(42, CancellationToken.None);

        await lease.ReleaseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(lease.IsHeld);
        Assert.Equal(1, store.ReleaseCount);
        blockedRelease.TrySetException(new InvalidOperationException("late release failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => blockedRelease.Task);
    }

    private static BeanBotInstanceLease CreateLease(CleanupLeaseStore store)
        => new(
            store,
            new NoOpHostApplicationLifetime(),
            new StaticLeaseClock(StartUtc),
            TestOptions,
            NullLogger<BeanBotInstanceLease>.Instance,
            "cleanup-test-holder");

    private sealed class CleanupLeaseStore : IInstanceLeaseStore
    {
        public Func<string, string, CancellationToken, Task<bool>> Release { get; init; }
            = static (_, _, _) => Task.FromResult(true);

        public int AcquireCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<InstanceLeaseAcquireResult> TryAcquireAsync(
            string botIdentity,
            string holderId,
            DateTime nowUtc,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            return Task.FromResult(InstanceLeaseAcquireResult.Acquired);
        }

        public Task<InstanceLeaseSnapshot?> GetAsync(
            string botIdentity,
            CancellationToken cancellationToken)
            => Task.FromResult<InstanceLeaseSnapshot?>(null);

        public Task<bool> TryRenewAsync(
            string botIdentity,
            string holderId,
            DateTime nowUtc,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryReleaseAsync(
            string botIdentity,
            string holderId,
            CancellationToken cancellationToken)
        {
            ReleaseCount++;
            return Release(botIdentity, holderId, cancellationToken);
        }
    }

    private sealed class StaticLeaseClock(DateTime utcNow) : IInstanceLeaseClock
    {
        public DateTime UtcNow { get; } = utcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class NoOpHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }
}
