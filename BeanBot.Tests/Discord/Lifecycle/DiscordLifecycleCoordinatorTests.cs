using BeanBot.Discord.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Lifecycle;

public class DiscordLifecycleCoordinatorTests
{
    [Fact]
    public async Task UnfinishedLogoutRetainsExclusiveClientOwnershipAndObservesLateFailure()
    {
        var logout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new DiscordLifecycleCoordinator(
            NullLogger<DiscordLifecycleCoordinator>.Instance,
            (exception, _, _) => observed.TrySetResult(exception));
        var calls = new List<string>();

        var recovery = await coordinator.RunSequenceAsync(
            "recovery",
            [
                new("stop", () => RecordCompleted(calls, "recovery-stop")),
                new("logout", () => Record(calls, "recovery-logout", logout.Task))
            ],
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);
        var shutdown = await coordinator.RunSequenceAsync(
            "shutdown",
            [new("logout", () => RecordCompleted(calls, "shutdown-logout"))],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(DiscordLifecycleOutcomeKind.Unfinished, recovery.Kind);
        Assert.Equal(DiscordLifecycleOutcomeKind.NeverStarted, shutdown.Kind);
        Assert.True(coordinator.HasActiveSequence);
        Assert.Equal(["recovery-stop", "recovery-logout"], calls);

        logout.SetException(new InvalidOperationException("late lifecycle failure"));
        Assert.IsType<AggregateException>(await observed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(() => !coordinator.HasActiveSequence);
    }

    [Fact]
    public async Task UnfinishedOperationLateFailure_UsesLoggingFallbackAndReleasesOwnership()
    {
        var operation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new DiscordLifecycleCoordinator(
            NullLogger<DiscordLifecycleCoordinator>.Instance);

        var outcome = await coordinator.RunSequenceAsync(
            "recovery",
            [new("logout", () => operation.Task)],
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.Equal(DiscordLifecycleOutcomeKind.Unfinished, outcome.Kind);
        Assert.True(coordinator.HasActiveSequence);

        operation.SetException(new InvalidOperationException("late failure"));
        await WaitUntilAsync(() => !coordinator.HasActiveSequence);
    }

    [Fact]
    public async Task CancellationAfterSequenceStartsLeavesOperationOwnedButDoesNotBlockLaterCleanup()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new DiscordLifecycleCoordinator(
            NullLogger<DiscordLifecycleCoordinator>.Instance);
        using var cancellation = new CancellationTokenSource();
        var laterCleanupRan = false;

        var lifecycle = coordinator.RunSequenceAsync(
            "recovery",
            [new("logout", () => { started.SetResult(); return operation.Task; })],
            TimeSpan.FromMinutes(1),
            cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        var outcome = await lifecycle.WaitAsync(TimeSpan.FromSeconds(2));
        laterCleanupRan = true;

        Assert.Equal(DiscordLifecycleOutcomeKind.Unfinished, outcome.Kind);
        Assert.True(laterCleanupRan);
        Assert.True(coordinator.HasActiveSequence);

        operation.SetResult();
        await WaitUntilAsync(() => !coordinator.HasActiveSequence);
    }

    [Fact]
    public async Task OutcomesDistinguishCompletedCompletedFailureAndNeverStarted()
    {
        var coordinator = new DiscordLifecycleCoordinator(
            NullLogger<DiscordLifecycleCoordinator>.Instance);
        var completed = await coordinator.RunSequenceAsync(
            "completed",
            [new("stop", () => Task.CompletedTask)],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        var expected = new InvalidOperationException("completed fault");
        var failed = await coordinator.RunSequenceAsync(
            "failed",
            [new("stop", () => Task.FromException(expected))],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var neverStarted = await coordinator.RunSequenceAsync(
            "canceled",
            [new("stop", () => Task.CompletedTask)],
            TimeSpan.FromSeconds(1),
            canceled.Token);

        Assert.Equal(DiscordLifecycleOutcomeKind.Completed, completed.Kind);
        Assert.Equal(DiscordLifecycleOutcomeKind.FailedAfterCompletion, failed.Kind);
        Assert.Same(expected, failed.Exception);
        Assert.Equal(DiscordLifecycleOutcomeKind.NeverStarted, neverStarted.Kind);
    }

    [Fact]
    public async Task CancellationBetweenStepsReportsNextOperationNeverStarted()
    {
        using var cancellation = new CancellationTokenSource();
        var secondStarted = false;
        var coordinator = new DiscordLifecycleCoordinator(
            NullLogger<DiscordLifecycleCoordinator>.Instance);

        var outcome = await coordinator.RunSequenceAsync(
            "recovery",
            [
                new("stop", () => { cancellation.Cancel(); return Task.CompletedTask; }),
                new("logout", () => { secondStarted = true; return Task.CompletedTask; })
            ],
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        Assert.Equal(DiscordLifecycleOutcomeKind.NeverStarted, outcome.Kind);
        Assert.Equal("logout", outcome.Operation);
        Assert.False(secondStarted);
    }

    [Fact]
    public async Task SynchronousBeginFailureReportsOperationNeverStarted()
    {
        var expected = new InvalidOperationException("synchronous begin failure");
        var coordinator = new DiscordLifecycleCoordinator(
            NullLogger<DiscordLifecycleCoordinator>.Instance);

        var outcome = await coordinator.RunSequenceAsync(
            "shutdown",
            [new("logout", () => throw expected)],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(DiscordLifecycleOutcomeKind.NeverStarted, outcome.Kind);
        Assert.Equal("logout", outcome.Operation);
        Assert.Same(expected, outcome.Exception);
    }

    [Fact]
    public async Task UnderlyingFaultThatCompletesDuringCancellationRaceIsObservedAndReported()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("fault won the cancellation race");
        using var cancellation = new CancellationTokenSource();
        var coordinator = new DiscordLifecycleCoordinator(
            NullLogger<DiscordLifecycleCoordinator>.Instance);

        var lifecycle = coordinator.RunSequenceAsync(
            "recovery",
            [new("logout", () => operation.Task)],
            TimeSpan.FromMinutes(1),
            cancellation.Token);
        // Register after WaitAsync has subscribed. Cancellation callbacks run in
        // reverse order, so the Discord operation faults immediately before the
        // bounded wait observes cancellation.
        using var faultRegistration = cancellation.Token.Register(
            () => operation.TrySetException(expected));
        cancellation.Cancel();
        var outcome = await lifecycle.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DiscordLifecycleOutcomeKind.FailedAfterCompletion, outcome.Kind);
        Assert.Same(expected, outcome.Exception);
        Assert.False(coordinator.HasActiveSequence);
    }

    private static Task RecordCompleted(List<string> calls, string call)
        => Record(calls, call, Task.CompletedTask);

    private static Task Record(List<string> calls, string call, Task task)
    {
        calls.Add(call);
        return task;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Yield();
        }

        Assert.True(predicate());
    }
}
