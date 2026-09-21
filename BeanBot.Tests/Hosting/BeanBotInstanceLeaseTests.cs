using System.Collections.Concurrent;
using BeanBot.Hosting;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotInstanceLeaseTests
{
    private static readonly DateTime StartUtc = new(2026, 9, 21, 14, 0, 0, DateTimeKind.Utc);
    private static readonly InstanceLeaseOptions TestOptions = new(
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(1));

    [Fact]
    public async Task AcquireAsync_AcquiredLeaseBecomesHealthyAndReleaseUsesExactHolder()
    {
        var store = new RecordingLeaseStore();
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-one");

        await lease.AcquireAsync(42, CancellationToken.None);

        Assert.True(lease.IsHeld);
        var acquire = Assert.Single(store.AcquireCalls);
        Assert.Equal("42", acquire.BotIdentity);
        Assert.Equal("holder-one", acquire.HolderId);
        Assert.Equal(StartUtc + TestOptions.LeaseDuration, acquire.ExpiresAtUtc);

        await lease.ReleaseAsync(CancellationToken.None);

        Assert.False(lease.IsHeld);
        var release = Assert.Single(store.ReleaseCalls);
        Assert.Equal(("42", "holder-one"), release);
        Assert.False(lifetime.StopRequested);
    }

    [Fact]
    public async Task AcquireAsync_ConflictingLeaseFailsClosedWithoutStartingRenewal()
    {
        var store = new RecordingLeaseStore
        {
            Acquire = static (_, _, _, _, _) => Task.FromResult(InstanceLeaseAcquireResult.Conflict)
        };
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-two");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease.AcquireAsync(42, CancellationToken.None));

        Assert.Contains("Another BeanBot process", exception.Message);
        Assert.False(lease.IsHeld);
        Assert.Equal(0, clock.DelayCount);
        Assert.Empty(store.ReleaseCalls);
    }

    [Fact]
    public async Task AcquireAsync_AmbiguousWriteConfirmedByExactReadStartsLease()
    {
        var store = new RecordingLeaseStore
        {
            Acquire = static (_, _, _, _, _) =>
                Task.FromException<InstanceLeaseAcquireResult>(new TimeoutException("ambiguous write")),
            Get = static (_, _) => Task.FromResult<InstanceLeaseSnapshot?>(
                new InstanceLeaseSnapshot("holder-three", StartUtc.AddSeconds(40)))
        };
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-three");

        await lease.AcquireAsync(42, CancellationToken.None);

        Assert.True(lease.IsHeld);
        Assert.Single(store.GetCalls);
    }

    [Fact]
    public async Task AcquireAsync_AmbiguousWriteWithUnknownReadFailsClosed()
    {
        var store = new RecordingLeaseStore
        {
            Acquire = static (_, _, _, _, _) =>
                Task.FromException<InstanceLeaseAcquireResult>(new TimeoutException("ambiguous write")),
            Get = static (_, _) =>
                Task.FromException<InstanceLeaseSnapshot?>(new InvalidOperationException("mongo unavailable"))
        };
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-four");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease.AcquireAsync(42, CancellationToken.None));

        Assert.Contains("could not positively confirm", exception.Message);
        Assert.False(lease.IsHeld);
        Assert.Single(store.GetCalls);
    }

    [Fact]
    public async Task AcquireAsync_UnresponsiveWriteIsApplicationBoundedAndCanBeReconciled()
    {
        var blockedAcquire = new TaskCompletionSource<InstanceLeaseAcquireResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RecordingLeaseStore
        {
            Acquire = (_, _, _, _, _) => blockedAcquire.Task,
            Get = static (_, _) => Task.FromResult<InstanceLeaseSnapshot?>(
                new InstanceLeaseSnapshot("holder-five", StartUtc.AddSeconds(40)))
        };
        var options = TestOptions with { OperationTimeout = TimeSpan.FromMilliseconds(25) };
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-five", options);

        await lease.AcquireAsync(42, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(lease.IsHeld);
        Assert.Single(store.GetCalls);
        blockedAcquire.TrySetException(new InvalidOperationException("late failure"));
    }

    [Fact]
    public async Task Renewal_IsSingleFlightAndSuccessfulRenewalExtendsOwnership()
    {
        var renewCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renewStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RecordingLeaseStore
        {
            Renew = (_, _, _, _, _) =>
            {
                renewStarted.TrySetResult();
                return renewCompletion.Task;
            }
        };
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-six");
        await lease.AcquireAsync(42, CancellationToken.None);

        clock.AdvanceAndCompleteNextDelay(TestOptions.RenewInterval);
        await renewStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Single(store.RenewCalls);
        Assert.True(lease.IsHeld);
        Assert.False(lifetime.StopRequested);

        renewCompletion.TrySetResult(true);
        await clock.WaitForDelayCountAsync(2);

        Assert.Single(store.RenewCalls);
        Assert.True(lease.IsHeld);
    }

    [Fact]
    public async Task Renewal_FencedByAnotherHolderRequestsShutdownImmediately()
    {
        var store = new RecordingLeaseStore
        {
            Renew = static (_, _, _, _, _) => Task.FromResult(false)
        };
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-seven");
        await lease.AcquireAsync(42, CancellationToken.None);

        clock.AdvanceAndCompleteNextDelay(TestOptions.RenewInterval);
        await lifetime.StopRequestedTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(lease.IsHeld);
        Assert.Single(store.RenewCalls);
    }

    [Fact]
    public async Task Renewal_AmbiguousWriteConfirmedByExactReadRemainsHealthy()
    {
        var store = new RecordingLeaseStore
        {
            Renew = static (_, _, _, _, _) =>
                Task.FromException<bool>(new TimeoutException("ambiguous renewal")),
            Get = static (_, _) => Task.FromResult<InstanceLeaseSnapshot?>(
                new InstanceLeaseSnapshot("holder-eight", StartUtc.AddSeconds(60)))
        };
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-eight");
        await lease.AcquireAsync(42, CancellationToken.None);

        clock.AdvanceAndCompleteNextDelay(TestOptions.RenewInterval);
        await clock.WaitForDelayCountAsync(2);

        Assert.True(lease.IsHeld);
        Assert.False(lifetime.StopRequested);
        Assert.Single(store.GetCalls);
    }

    [Fact]
    public async Task Renewal_UncertainUntilSafetyDeadlineRequestsShutdownBeforeExpiry()
    {
        var store = new RecordingLeaseStore
        {
            Renew = static (_, _, _, _, _) =>
                Task.FromException<bool>(new TimeoutException("ambiguous renewal")),
            Get = static (_, _) =>
                Task.FromException<InstanceLeaseSnapshot?>(new InvalidOperationException("mongo unavailable"))
        };
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-nine");
        await lease.AcquireAsync(42, CancellationToken.None);

        clock.AdvanceAndCompleteNextDelay(TestOptions.RenewInterval);
        await clock.WaitForDelayCountAsync(2);
        clock.SetUtcNow(StartUtc + TestOptions.LeaseDuration - TestOptions.SafetyMargin);
        clock.CompleteNextDelay();

        await lifetime.StopRequestedTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(lease.IsHeld);
    }

    [Fact]
    public async Task ReleaseAsync_IsIdempotentAndNeverDeletesAnotherHolder()
    {
        var store = new RecordingLeaseStore
        {
            Release = static (_, _, _) => Task.FromResult(false)
        };
        var clock = new ControlledLeaseClock(StartUtc);
        var lifetime = new RecordingHostApplicationLifetime();
        await using var lease = CreateLease(store, clock, lifetime, "holder-ten");
        await lease.AcquireAsync(42, CancellationToken.None);

        await lease.ReleaseAsync(CancellationToken.None);
        await lease.ReleaseAsync(CancellationToken.None);

        var release = Assert.Single(store.ReleaseCalls);
        Assert.Equal(("42", "holder-ten"), release);
        Assert.False(lease.IsHeld);
    }

    private static BeanBotInstanceLease CreateLease(
        RecordingLeaseStore store,
        ControlledLeaseClock clock,
        RecordingHostApplicationLifetime lifetime,
        string holderId,
        InstanceLeaseOptions? options = null)
        => new(
            store,
            lifetime,
            clock,
            options ?? TestOptions,
            NullLogger<BeanBotInstanceLease>.Instance,
            holderId);

    private sealed class RecordingLeaseStore : IInstanceLeaseStore
    {
        public Func<string, string, DateTime, DateTime, CancellationToken, Task<InstanceLeaseAcquireResult>> Acquire { get; init; }
            = static (_, _, _, _, _) => Task.FromResult(InstanceLeaseAcquireResult.Acquired);
        public Func<string, CancellationToken, Task<InstanceLeaseSnapshot?>> Get { get; init; }
            = static (_, _) => Task.FromResult<InstanceLeaseSnapshot?>(null);
        public Func<string, string, DateTime, DateTime, CancellationToken, Task<bool>> Renew { get; init; }
            = static (_, _, _, _, _) => Task.FromResult(true);
        public Func<string, string, CancellationToken, Task<bool>> Release { get; init; }
            = static (_, _, _) => Task.FromResult(true);

        public List<LeaseMutationCall> AcquireCalls { get; } = [];
        public List<string> GetCalls { get; } = [];
        public List<LeaseMutationCall> RenewCalls { get; } = [];
        public List<(string BotIdentity, string HolderId)> ReleaseCalls { get; } = [];

        public Task<InstanceLeaseAcquireResult> TryAcquireAsync(
            string botIdentity,
            string holderId,
            DateTime nowUtc,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken)
        {
            AcquireCalls.Add(new LeaseMutationCall(botIdentity, holderId, nowUtc, expiresAtUtc));
            return Acquire(botIdentity, holderId, nowUtc, expiresAtUtc, cancellationToken);
        }

        public Task<InstanceLeaseSnapshot?> GetAsync(
            string botIdentity,
            CancellationToken cancellationToken)
        {
            GetCalls.Add(botIdentity);
            return Get(botIdentity, cancellationToken);
        }

        public Task<bool> TryRenewAsync(
            string botIdentity,
            string holderId,
            DateTime nowUtc,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken)
        {
            RenewCalls.Add(new LeaseMutationCall(botIdentity, holderId, nowUtc, expiresAtUtc));
            return Renew(botIdentity, holderId, nowUtc, expiresAtUtc, cancellationToken);
        }

        public Task<bool> TryReleaseAsync(
            string botIdentity,
            string holderId,
            CancellationToken cancellationToken)
        {
            ReleaseCalls.Add((botIdentity, holderId));
            return Release(botIdentity, holderId, cancellationToken);
        }
    }

    private sealed record LeaseMutationCall(
        string BotIdentity,
        string HolderId,
        DateTime NowUtc,
        DateTime ExpiresAtUtc);

    private sealed class ControlledLeaseClock : IInstanceLeaseClock
    {
        private readonly ConcurrentQueue<PendingDelay> _delays = new();
        private readonly SemaphoreSlim _delayAdded = new(0);
        private DateTime _utcNow;
        private int _delayCount;

        public ControlledLeaseClock(DateTime utcNow)
        {
            _utcNow = utcNow;
        }

        public DateTime UtcNow => _utcNow;
        public int DelayCount => Volatile.Read(ref _delayCount);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                completion);
            _delays.Enqueue(new PendingDelay(delay, completion, registration));
            Interlocked.Increment(ref _delayCount);
            _delayAdded.Release();
            return completion.Task;
        }

        public void AdvanceAndCompleteNextDelay(TimeSpan advance)
        {
            _utcNow += advance;
            CompleteNextDelay();
        }

        public void SetUtcNow(DateTime utcNow) => _utcNow = utcNow;

        public void CompleteNextDelay()
        {
            Assert.True(_delays.TryDequeue(out var delay), "No pending lease delay was available.");
            delay.Registration.Dispose();
            delay.Completion.TrySetResult();
        }

        public async Task WaitForDelayCountAsync(int expected)
        {
            while (DelayCount < expected)
            {
                await _delayAdded.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }

        private sealed record PendingDelay(
            TimeSpan Delay,
            TaskCompletionSource Completion,
            CancellationTokenRegistration Registration);
    }

    private sealed class RecordingHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly TaskCompletionSource _stopRequested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool StopRequested { get; private set; }
        public Task StopRequestedTask => _stopRequested.Task;

        public void StopApplication()
        {
            StopRequested = true;
            _stopRequested.TrySetResult();
        }
    }
}
