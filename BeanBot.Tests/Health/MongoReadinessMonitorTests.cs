using BeanBot.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Health;

public class MongoReadinessMonitorTests
{
    private static readonly DateTimeOffset InitialTime = new(
        2026,
        9,
        3,
        14,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task CompletedSuccess_IsCachedUntilFreshnessWindowExpires()
    {
        var clock = new ManualTimeProvider(InitialTime);
        var probe = new ScriptedProbe(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        var monitor = CreateMonitor(
            probe,
            clock,
            probeTimeout: TimeSpan.FromSeconds(1),
            freshnessWindow: TimeSpan.FromSeconds(10));

        var first = await monitor.GetSnapshotAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(9));
        var cached = await monitor.GetSnapshotAsync(CancellationToken.None);

        Assert.True(first.IsReachable);
        Assert.True(cached.IsReachable);
        Assert.Equal(1, probe.CallCount);

        clock.Advance(TimeSpan.FromSeconds(2));
        var refreshed = await monitor.GetSnapshotAsync(CancellationToken.None);

        Assert.True(refreshed.IsReachable);
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task ConcurrentRequests_ShareOneOutstandingProbe()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ScriptedProbe(_ => completion.Task);
        var monitor = CreateMonitor(
            probe,
            new ManualTimeProvider(InitialTime),
            probeTimeout: TimeSpan.FromSeconds(1),
            freshnessWindow: TimeSpan.FromSeconds(10));

        var requests = Enumerable.Range(0, 8)
            .Select(_ => monitor.GetSnapshotAsync(CancellationToken.None))
            .ToArray();
        await WaitUntilAsync(() => probe.CallCount == 1);

        completion.TrySetResult();
        var snapshots = await Task.WhenAll(requests);

        Assert.All(snapshots, snapshot => Assert.True(snapshot.IsReachable));
        Assert.Equal(1, probe.CallCount);
        Assert.False(monitor.HasInFlightProbe);
    }

    [Fact]
    public async Task StalledProbe_TimesOutWithoutStartingOverlappingProbe_AndLateFaultIsObserved()
    {
        var clock = new ManualTimeProvider(InitialTime);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCancellation = CancellationToken.None;
        var probe = new ScriptedProbe(
            cancellationToken =>
            {
                probeCancellation = cancellationToken;
                return stalled.Task;
            },
            _ => Task.CompletedTask);
        var monitor = CreateMonitor(
            probe,
            clock,
            probeTimeout: TimeSpan.FromMilliseconds(50),
            freshnessWindow: TimeSpan.FromSeconds(10));

        var timedOut = await monitor
            .GetSnapshotAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(timedOut.IsReachable);
        Assert.True(probeCancellation.IsCancellationRequested);
        Assert.True(monitor.HasInFlightProbe);
        Assert.Equal(1, probe.CallCount);

        clock.Advance(TimeSpan.FromSeconds(11));
        var stillTimedOut = await monitor.GetSnapshotAsync(CancellationToken.None);

        Assert.False(stillTimedOut.IsReachable);
        Assert.Equal(1, probe.CallCount);

        stalled.TrySetException(new InvalidOperationException(
            "mongodb://user:password@should-never-be-reported"));
        await WaitUntilAsync(() => !monitor.HasInFlightProbe);

        clock.Advance(TimeSpan.FromSeconds(11));
        var recovered = await monitor.GetSnapshotAsync(CancellationToken.None);

        Assert.True(recovered.IsReachable);
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task ProbeCompletionRacingDeadline_DoesNotThrowAfterTimeoutSourceIsDisposed()
    {
        var clock = new SequencedTimeProvider(
            InitialTime,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2));
        var probe = new ScriptedProbe(_ => Task.CompletedTask);
        var monitor = CreateMonitor(
            probe,
            clock,
            probeTimeout: TimeSpan.FromSeconds(1),
            freshnessWindow: TimeSpan.FromSeconds(10));

        var timedOutWaiter = await monitor.GetSnapshotAsync(CancellationToken.None);
        var completedProbeSnapshot = await monitor.GetSnapshotAsync(CancellationToken.None);

        Assert.False(timedOutWaiter.IsReachable);
        Assert.True(completedProbeSnapshot.IsReachable);
        Assert.False(monitor.HasInFlightProbe);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task StaleSuccess_IsNotTrustedWhenRefreshCannotComplete()
    {
        var clock = new ManualTimeProvider(InitialTime);
        var probe = new ScriptedProbe(
            _ => Task.CompletedTask,
            cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var monitor = CreateMonitor(
            probe,
            clock,
            probeTimeout: TimeSpan.FromMilliseconds(50),
            freshnessWindow: TimeSpan.FromSeconds(10));

        var healthy = await monitor.GetSnapshotAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(11));
        var unavailable = await monitor
            .GetSnapshotAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(healthy.IsReachable);
        Assert.False(unavailable.IsReachable);
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task RequestCancellation_StopsOnlyThatWaiter_AndSharedProbeCanStillSucceed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCancellation = CancellationToken.None;
        var probe = new ScriptedProbe(cancellationToken =>
        {
            probeCancellation = cancellationToken;
            return completion.Task;
        });
        var monitor = CreateMonitor(
            probe,
            new ManualTimeProvider(InitialTime),
            probeTimeout: TimeSpan.FromSeconds(5),
            freshnessWindow: TimeSpan.FromSeconds(10));
        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => monitor.GetSnapshotAsync(requestCancellation.Token));

        Assert.Equal(1, probe.CallCount);
        Assert.False(probeCancellation.IsCancellationRequested);
        Assert.True(monitor.HasInFlightProbe);

        completion.TrySetResult();
        await WaitUntilAsync(() => !monitor.HasInFlightProbe);
        var recovered = await monitor.GetSnapshotAsync(CancellationToken.None);

        Assert.True(recovered.IsReachable);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task CompletedFailure_CanRecoverAfterFreshnessWindow()
    {
        var clock = new ManualTimeProvider(InitialTime);
        var probe = new ScriptedProbe(
            _ => Task.FromException(new InvalidOperationException(
                "mongodb://user:password@should-never-be-reported")),
            _ => Task.CompletedTask);
        var monitor = CreateMonitor(
            probe,
            clock,
            probeTimeout: TimeSpan.FromSeconds(1),
            freshnessWindow: TimeSpan.FromSeconds(10));

        var failed = await monitor.GetSnapshotAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(11));
        var recovered = await monitor.GetSnapshotAsync(CancellationToken.None);

        Assert.False(failed.IsReachable);
        Assert.True(recovered.IsReachable);
        Assert.Equal(2, probe.CallCount);
    }

    private static MongoReadinessMonitor CreateMonitor(
        IMongoReadinessProbe probe,
        TimeProvider timeProvider,
        TimeSpan probeTimeout,
        TimeSpan freshnessWindow)
        => new(
            probe,
            NullLogger<MongoReadinessMonitor>.Instance,
            timeProvider,
            probeTimeout,
            freshnessWindow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < timeoutAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }

        Assert.True(condition(), "Condition was not reached before the test timeout.");
    }

    private sealed class ScriptedProbe : IMongoReadinessProbe
    {
        private readonly object _syncRoot = new();
        private readonly Queue<Func<CancellationToken, Task>> _steps;
        private int _callCount;

        public ScriptedProbe(params Func<CancellationToken, Task>[] steps)
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

    private sealed class SequencedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        private readonly Queue<long> _timestamps;
        private long _lastTimestamp;

        public SequencedTimeProvider(DateTimeOffset utcNow, params TimeSpan[] timestamps)
        {
            _utcNow = utcNow;
            _timestamps = new Queue<long>(timestamps.Select(timestamp => timestamp.Ticks));
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp()
        {
            if (_timestamps.Count > 0)
            {
                _lastTimestamp = _timestamps.Dequeue();
            }

            return _lastTimestamp;
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
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            lock (_syncRoot)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks;
            }
        }
    }
}
