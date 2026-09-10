using BeanBot.Discord.Commands;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class ExternalMediaAdmissionGuardTests
{
    private const string BudgetKey = "external-media";
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunAsync_FirstInvocation_IsAccepted()
    {
        var guard = CreateGuard(new ManualTimeProvider());
        var calls = 0;

        var result = await guard.RunAsync(
            1,
            BudgetKey,
            () =>
            {
                calls++;
                return Task.CompletedTask;
            });

        Assert.Equal(ExternalMediaAdmissionResult.Accepted, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_ConcurrentDuplicate_DoesNotStartSecondOperation()
    {
        var guard = CreateGuard(new ManualTimeProvider());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var first = guard.RunAsync(
            1,
            BudgetKey,
            async () =>
            {
                calls++;
                started.SetResult();
                await release.Task;
            });
        await started.Task;

        var second = await guard.RunAsync(
            1,
            BudgetKey,
            () =>
            {
                calls++;
                return Task.CompletedTask;
            });

        Assert.Equal(ExternalMediaAdmissionResult.InFlight, second);
        Assert.Equal(1, calls);

        release.SetResult();
        Assert.Equal(ExternalMediaAdmissionResult.Accepted, await first);
    }

    [Fact]
    public async Task RunAsync_RapidFollowUp_IsRejectedUntilCooldownExpires()
    {
        var timeProvider = new ManualTimeProvider();
        var guard = CreateGuard(timeProvider);
        var calls = 0;

        Assert.Equal(
            ExternalMediaAdmissionResult.Accepted,
            await guard.RunAsync(1, BudgetKey, CountCall));
        Assert.Equal(
            ExternalMediaAdmissionResult.CoolingDown,
            await guard.RunAsync(1, BudgetKey, CountCall));
        Assert.Equal(1, calls);

        timeProvider.Advance(Cooldown);

        Assert.Equal(
            ExternalMediaAdmissionResult.Accepted,
            await guard.RunAsync(1, BudgetKey, CountCall));
        Assert.Equal(2, calls);
        return;

        Task CountCall()
        {
            calls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_DifferentUsers_DoNotBlockEachOther()
    {
        var guard = CreateGuard(new ManualTimeProvider());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstUser = guard.RunAsync(
            1,
            BudgetKey,
            async () =>
            {
                started.SetResult();
                await release.Task;
            });
        await started.Task;

        var secondUser = await guard.RunAsync(2, BudgetKey, () => Task.CompletedTask);

        Assert.Equal(ExternalMediaAdmissionResult.Accepted, secondUser);

        release.SetResult();
        Assert.Equal(ExternalMediaAdmissionResult.Accepted, await firstUser);
    }

    [Fact]
    public async Task RunAsync_DifferentBudgetKeys_AreIndependent()
    {
        var guard = CreateGuard(new ManualTimeProvider());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var sharedBudget = guard.RunAsync(
            1,
            BudgetKey,
            async () =>
            {
                started.SetResult();
                await release.Task;
            });
        await started.Task;

        var differentBudget = await guard.RunAsync(1, "other-budget", () => Task.CompletedTask);

        Assert.Equal(ExternalMediaAdmissionResult.Accepted, differentBudget);

        release.SetResult();
        Assert.Equal(ExternalMediaAdmissionResult.Accepted, await sharedBudget);
    }

    [Fact]
    public async Task RunAsync_Failure_ReleasesInFlightLeaseAndKeepsCooldown()
    {
        var timeProvider = new ManualTimeProvider();
        var guard = CreateGuard(timeProvider);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.RunAsync(
                1,
                BudgetKey,
                () => Task.FromException(new InvalidOperationException("failure"))));

        Assert.Equal(
            ExternalMediaAdmissionResult.CoolingDown,
            await guard.RunAsync(1, BudgetKey, () => Task.CompletedTask));

        timeProvider.Advance(Cooldown);

        Assert.Equal(
            ExternalMediaAdmissionResult.Accepted,
            await guard.RunAsync(1, BudgetKey, () => Task.CompletedTask));
    }

    [Fact]
    public async Task RunAsync_Timeout_ReleasesInFlightLeaseAndKeepsCooldown()
    {
        var timeProvider = new ManualTimeProvider();
        var guard = CreateGuard(timeProvider);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            guard.RunAsync(
                1,
                BudgetKey,
                () => Task.FromException(new TimeoutException("timeout"))));

        Assert.Equal(
            ExternalMediaAdmissionResult.CoolingDown,
            await guard.RunAsync(1, BudgetKey, () => Task.CompletedTask));

        timeProvider.Advance(Cooldown);

        Assert.Equal(
            ExternalMediaAdmissionResult.Accepted,
            await guard.RunAsync(1, BudgetKey, () => Task.CompletedTask));
    }

    [Fact]
    public async Task RunAsync_Cancellation_ReleasesInFlightLeaseAndKeepsCooldown()
    {
        var timeProvider = new ManualTimeProvider();
        var guard = CreateGuard(timeProvider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            guard.RunAsync(
                1,
                BudgetKey,
                () => Task.FromCanceled(cancellation.Token)));

        Assert.Equal(
            ExternalMediaAdmissionResult.CoolingDown,
            await guard.RunAsync(1, BudgetKey, () => Task.CompletedTask));

        timeProvider.Advance(Cooldown);

        Assert.Equal(
            ExternalMediaAdmissionResult.Accepted,
            await guard.RunAsync(1, BudgetKey, () => Task.CompletedTask));
    }

    [Fact]
    public async Task RunAsync_CapacityIsHardBoundAndExpiredEntriesAreReclaimed()
    {
        var timeProvider = new ManualTimeProvider();
        var guard = CreateGuard(timeProvider, capacity: 2);

        Assert.Equal(
            ExternalMediaAdmissionResult.Accepted,
            await guard.RunAsync(1, BudgetKey, () => Task.CompletedTask));
        Assert.Equal(
            ExternalMediaAdmissionResult.Accepted,
            await guard.RunAsync(2, BudgetKey, () => Task.CompletedTask));
        Assert.Equal(
            ExternalMediaAdmissionResult.CapacityReached,
            await guard.RunAsync(3, BudgetKey, () => Task.CompletedTask));

        timeProvider.Advance(Cooldown);

        Assert.Equal(
            ExternalMediaAdmissionResult.Accepted,
            await guard.RunAsync(3, BudgetKey, () => Task.CompletedTask));
    }

    private static ExternalMediaAdmissionGuard CreateGuard(
        ManualTimeProvider timeProvider,
        int capacity = 16)
        => new(
            new ExternalMediaCommandOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                1024,
                Cooldown,
                capacity),
            timeProvider);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        internal void Advance(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            Interlocked.Add(ref _timestamp, duration.Ticks);
        }
    }
}
