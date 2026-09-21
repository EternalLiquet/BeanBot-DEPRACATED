using System.Collections.Concurrent;
using System.Threading.Channels;
using BeanBot.Discord.Lifecycle;
using BeanBot.Hosting;
using BeanBot.Logging;
using BeanBot.Persistence.Outages;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class InstanceLeaseReviewRegressionTests
{
    private static readonly DateTime StartUtc = new(2026, 9, 21, 14, 0, 0, DateTimeKind.Utc);
    private static readonly InstanceLeaseOptions TestOptions = new(
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(1));

    [Fact]
    public async Task Renewal_AmbiguousWriteShowingOnlyOldExpiryRetriesOnUncertainCadence()
    {
        var originalExpiry = StartUtc + TestOptions.LeaseDuration;
        var store = new LeaseStore
        {
            Renew = static (_, _, _, _, _) =>
                Task.FromException<bool>(new TimeoutException("ambiguous renewal")),
            Get = (_, _) => Task.FromResult<InstanceLeaseSnapshot?>(
                new InstanceLeaseSnapshot("review-holder", originalExpiry))
        };
        var clock = new ControlledClock(StartUtc);
        var lifetime = new RecordingLifetime();
        await using var lease = new BeanBotInstanceLease(
            store,
            lifetime,
            clock,
            TestOptions,
            NullLogger<BeanBotInstanceLease>.Instance,
            "review-holder");
        await lease.AcquireAsync(42, CancellationToken.None);

        clock.AdvanceAndCompleteNextDelay(TestOptions.RenewInterval);
        await clock.WaitForDelayCountAsync(2);

        Assert.Equal(TestOptions.UncertainRetryDelay, clock.NextDelay);
        Assert.True(lease.IsHeld);
        Assert.False(lifetime.StopRequested);
    }

    [Fact]
    public async Task SuppressRelease_DisposeStopsRenewalWithoutDeletingLease()
    {
        var store = new LeaseStore();
        var clock = new ControlledClock(StartUtc);
        var lifetime = new RecordingLifetime();
        var lease = new BeanBotInstanceLease(
            store,
            lifetime,
            clock,
            TestOptions,
            NullLogger<BeanBotInstanceLease>.Instance,
            "review-holder");
        await lease.AcquireAsync(42, CancellationToken.None);

        lease.SuppressRelease();
        await lease.DisposeAsync();

        Assert.False(lease.IsHeld);
        Assert.Equal(0, store.ReleaseCount);
    }

    [Fact]
    public async Task StartAsync_CancellationAfterCommandInitializationDoesNotStartEventServices()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new StartupRuntime(cancellation.Cancel);
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => application.StartAsync(cancellation.Token));

        Assert.Contains("start-commands", runtime.Calls);
        Assert.DoesNotContain("start-events", runtime.Calls);
    }

    [Fact]
    public async Task OwnerAlerts_DisabledUntilLeaseAdmissionIsEnabled()
    {
        var delivery = new RecordingOwnerDelivery();
        await using var notifier = new DiscordOwnerErrorNotifier(
            delivery,
            _ => TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100),
            startAccepting: false);

        notifier.Enqueue("before lease");
        notifier.StartAccepting();
        notifier.Enqueue("after lease");
        await notifier.FlushAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(["after lease"], delivery.Messages);
    }

    [Fact]
    public async Task OutageRecovery_CanceledWaitKeepsLateDiscordDeliveryOwned()
    {
        var outage = new DiscordOutage
        {
            DisconnectedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            MostRecentDisconnectReason = "test outage"
        };
        var store = new OutageStore(outage);
        var delivery = new BlockingOwnerDelivery();
        using var notifier = new DiscordOutageRecoveryNotifier(
            store,
            delivery,
            NullLogger<DiscordOutageRecoveryNotifier>.Instance,
            _ => TimeSpan.Zero,
            TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();

        var notification = notifier.NotifyIfOutageRecoveredAsync(
            DateTimeOffset.UtcNow,
            cancellation.Token);
        await delivery.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(notifier.HasActiveDiscordOperation);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification);

        Assert.True(notifier.HasActiveDiscordOperation);
        delivery.Complete();
    }

    private sealed class LeaseStore : IInstanceLeaseStore
    {
        public Func<string, CancellationToken, Task<InstanceLeaseSnapshot?>> Get { get; init; }
            = static (_, _) => Task.FromResult<InstanceLeaseSnapshot?>(null);
        public Func<string, string, DateTime, DateTime, CancellationToken, Task<bool>> Renew { get; init; }
            = static (_, _, _, _, _) => Task.FromResult(true);
        public int ReleaseCount { get; private set; }

        public Task<InstanceLeaseAcquireResult> TryAcquireAsync(
            string botIdentity,
            string holderId,
            DateTime nowUtc,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken)
            => Task.FromResult(InstanceLeaseAcquireResult.Acquired);

        public Task<InstanceLeaseSnapshot?> GetAsync(
            string botIdentity,
            CancellationToken cancellationToken)
            => Get(botIdentity, cancellationToken);

        public Task<bool> TryRenewAsync(
            string botIdentity,
            string holderId,
            DateTime nowUtc,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken)
            => Renew(botIdentity, holderId, nowUtc, expiresAtUtc, cancellationToken);

        public Task<bool> TryReleaseAsync(
            string botIdentity,
            string holderId,
            CancellationToken cancellationToken)
        {
            ReleaseCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class ControlledClock : IInstanceLeaseClock
    {
        private readonly ConcurrentQueue<PendingDelay> _delays = new();
        private readonly Channel<bool> _delayAdded = Channel.CreateUnbounded<bool>();
        private DateTime _utcNow;
        private int _delayCount;

        public ControlledClock(DateTime utcNow) => _utcNow = utcNow;

        public DateTime UtcNow => _utcNow;
        public int DelayCount => Volatile.Read(ref _delayCount);
        public TimeSpan NextDelay
        {
            get
            {
                Assert.True(_delays.TryPeek(out var delay), "No pending lease delay was available.");
                return delay.Delay;
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                completion);
            _delays.Enqueue(new PendingDelay(delay, completion, registration));
            Interlocked.Increment(ref _delayCount);
            _delayAdded.Writer.TryWrite(true);
            return completion.Task;
        }

        public void AdvanceAndCompleteNextDelay(TimeSpan advance)
        {
            _utcNow += advance;
            Assert.True(_delays.TryDequeue(out var delay), "No pending lease delay was available.");
            delay.Registration.Dispose();
            delay.Completion.TrySetResult();
        }

        public async Task WaitForDelayCountAsync(int expected)
        {
            while (DelayCount < expected)
            {
                await _delayAdded.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }
        }

        private sealed record PendingDelay(
            TimeSpan Delay,
            TaskCompletionSource Completion,
            CancellationTokenRegistration Registration);
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool StopRequested { get; private set; }
        public void StopApplication() => StopRequested = true;
    }

    private sealed class StartupRuntime(Action onStartCommands) : IBeanBotRuntime
    {
        public List<string> Calls { get; } = [];
        public bool HasActiveDiscordLifecycleOperation => false;
        public bool CanDisposeDiscordClient => true;
        public void SubscribeApplicationEvents() => Calls.Add("subscribe");
        public Task StartHealthServerAsync(CancellationToken cancellationToken) => RecordAsync("health");
        public Task StartDiscordAsync(CancellationToken cancellationToken) => RecordAsync("discord");
        public Task AcquireInstanceLeaseAsync(CancellationToken cancellationToken) => RecordAsync("lease");
        public void StartGatewayRecovery() => Calls.Add("recovery");
        public Task StartCommandServicesAsync()
        {
            Calls.Add("start-commands");
            onStartCommands();
            return Task.CompletedTask;
        }
        public void StartEventAndBackgroundServices() => Calls.Add("start-events");
        public void StopReactionServices() { }
        public void StopNewMemberEvents() { }
        public Task StopEditedMessageEventsAsync() => Task.CompletedTask;
        public Task<bool> StopCommandServicesAsync() => Task.FromResult(true);
        public Task StopCommandRepliesAsync() => Task.CompletedTask;
        public void StopMessageWaiter() { }
        public void StopPaginator() { }
        public void UnsubscribeDiscordLog() { }
        public Task StopGatewayRecoveryAsync() => Task.CompletedTask;
        public void UnsubscribeApplicationEvents() { }
        public Task StopPunServiceAsync() => Task.CompletedTask;
        public Task ReleaseInstanceLeaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void SkipInstanceLeaseRelease() { }
        public Task StopHealthServerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushOwnerAlertsAsync() => Task.CompletedTask;
        public Task StopOwnerAlertsAsync() => Task.CompletedTask;
        public Task StopDiscordAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void DisposeDiscordClient() { }

        private Task RecordAsync(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOwnerDelivery : IOwnerAlertDelivery
    {
        public List<string> Messages { get; } = [];
        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            Messages.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingOwnerDelivery : IOwnerAlertDelivery
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return _completion.Task;
        }

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class OutageStore(DiscordOutage outage) : IDiscordOutageStore
    {
        public Task<DiscordOutage?> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DiscordOutage?>(outage);
        public Task OpenAsync(
            DateTimeOffset disconnectedAtUtc,
            string? reason,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task MarkManualRecoveryAttemptedAsync(
            DateTimeOffset disconnectedAtUtc,
            string? reason,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task MarkProcessRestartRequestedAsync(
            DateTimeOffset disconnectedAtUtc,
            string? reason,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
