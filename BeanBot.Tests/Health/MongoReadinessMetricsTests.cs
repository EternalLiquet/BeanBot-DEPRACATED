using BeanBot.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Health;

public class MongoReadinessMetricsTests
{
    private static readonly DateTimeOffset InitialTime = new(
        2026,
        9,
        17,
        13,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void BeforeFirstProbe_MetricsAreUnknownWithoutStartingWork()
    {
        var probe = new CountingProbe(_ => Task.CompletedTask);
        var monitor = CreateMonitor(probe, new ManualTimeProvider(InitialTime));

        var metrics = monitor.CreateMetricsSnapshot();

        Assert.False(metrics.IsKnown);
        Assert.False(metrics.IsReachable);
        Assert.False(metrics.IsFresh);
        Assert.Null(metrics.LastCheckedAtUtc);
        Assert.Equal(0, metrics.SuccessCount);
        Assert.Equal(0, metrics.FailureCount);
        Assert.Equal(0, metrics.TimeoutCount);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task CompletedOutcomes_AreCountedOnceAndFreshnessUsesReadinessWindow()
    {
        var clock = new ManualTimeProvider(InitialTime);
        var probe = new CountingProbe(
            _ => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException("private failure text")));
        var monitor = CreateMonitor(probe, clock);

        await monitor.GetSnapshotAsync(CancellationToken.None);
        var healthy = monitor.CreateMetricsSnapshot();

        Assert.True(healthy.IsKnown);
        Assert.True(healthy.IsReachable);
        Assert.True(healthy.IsFresh);
        Assert.Equal(1, healthy.SuccessCount);
        Assert.Equal(0, healthy.FailureCount);
        Assert.Equal(0, healthy.TimeoutCount);

        clock.Advance(TimeSpan.FromSeconds(11));
        var stale = monitor.CreateMetricsSnapshot();
        Assert.False(stale.IsFresh);
        Assert.Equal(1, probe.CallCount);

        await monitor.GetSnapshotAsync(CancellationToken.None);
        var failed = monitor.CreateMetricsSnapshot();

        Assert.True(failed.IsKnown);
        Assert.False(failed.IsReachable);
        Assert.True(failed.IsFresh);
        Assert.Equal(1, failed.SuccessCount);
        Assert.Equal(1, failed.FailureCount);
        Assert.Equal(0, failed.TimeoutCount);
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task TimeoutOutcome_IsCountedOnceWhileLateProbeRemainsOwned()
    {
        var clock = new ManualTimeProvider(InitialTime);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new CountingProbe(_ => completion.Task);
        var monitor = new MongoReadinessMonitor(
            probe,
            NullLogger<MongoReadinessMonitor>.Instance,
            clock,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(10));

        var timedOut = await monitor.GetSnapshotAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        var afterTimeout = monitor.CreateMetricsSnapshot();
        var cached = await monitor.GetSnapshotAsync(CancellationToken.None);
        var afterCachedRead = monitor.CreateMetricsSnapshot();

        Assert.False(timedOut.IsReachable);
        Assert.False(cached.IsReachable);
        Assert.True(monitor.HasInFlightProbe);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(1, afterTimeout.TimeoutCount);
        Assert.Equal(1, afterCachedRead.TimeoutCount);
        Assert.Equal(0, afterCachedRead.SuccessCount);
        Assert.Equal(0, afterCachedRead.FailureCount);

        completion.TrySetException(new InvalidOperationException("late private failure"));
        await WaitUntilAsync(() => !monitor.HasInFlightProbe);

        Assert.Equal(1, monitor.CreateMetricsSnapshot().TimeoutCount);
    }

    private static MongoReadinessMonitor CreateMonitor(
        IMongoReadinessProbe probe,
        TimeProvider timeProvider)
        => new(
            probe,
            NullLogger<MongoReadinessMonitor>.Instance,
            timeProvider,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < timeoutAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }

        Assert.True(condition(), "Condition was not reached before the test timeout.");
    }

    private sealed class CountingProbe : IMongoReadinessProbe
    {
        private readonly object _syncRoot = new();
        private readonly Queue<Func<CancellationToken, Task>> _steps;
        private int _callCount;

        public CountingProbe(params Func<CancellationToken, Task>[] steps)
        {
            _steps = new Queue<Func<CancellationToken, Task>>(steps);
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public Task CheckAsync(CancellationToken cancellationToken)
        {
            Func<CancellationToken, Task> step;
            lock (_syncRoot)
            {
                if (_steps.Count == 0)
                {
                    throw new InvalidOperationException("No scripted Mongo readiness probe step remains.");
                }

                step = _steps.Dequeue();
                Interlocked.Increment(ref _callCount);
            }

            return step(cancellationToken);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _syncRoot = new();
        private DateTimeOffset _utcNow;
        private long _timestamp;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_syncRoot)
            {
                return _utcNow;
            }
        }

        public override long GetTimestamp()
        {
            lock (_syncRoot)
            {
                return _timestamp;
            }
        }

        public void Advance(TimeSpan elapsed)
        {
            lock (_syncRoot)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks;
            }
        }
    }
}
