using BeanBot.Discord.Events;
using Xunit;

namespace BeanBot.Tests.Discord.Events;

public class LegacyCommandExecutionCoordinatorTests
{
    [Fact]
    public async Task TryExecuteAsync_NormalExecution_RunsOnceAndReleasesCapacity()
    {
        var coordinator = new LegacyCommandExecutionCoordinator(
            maximumConcurrentExecutions: 2,
            drainTimeout: TimeSpan.FromSeconds(1));
        var executionCount = 0;

        var result = await coordinator.TryExecuteAsync(() =>
        {
            Interlocked.Increment(ref executionCount);
            return Task.CompletedTask;
        });

        Assert.Equal(LegacyCommandAdmissionResult.Executed, result);
        Assert.Equal(1, executionCount);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task TryExecuteAsync_AtCapacity_RejectsWithoutQueueingAndKeepsBookkeepingBounded()
    {
        var coordinator = new LegacyCommandExecutionCoordinator(
            maximumConcurrentExecutions: 2,
            drainTimeout: TimeSpan.FromSeconds(1));
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        Task<LegacyCommandAdmissionResult> StartExecution()
            => coordinator.TryExecuteAsync(async () =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    bothStarted.TrySetResult();
                }

                await release.Task;
            });

        var first = StartExecution();
        var second = StartExecution();
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, coordinator.ActiveExecutionCount);
        Assert.Equal(coordinator.MaximumConcurrentExecutions, coordinator.ActiveExecutionCount);

        var rejectedExecutionCount = 0;
        var rejected = await coordinator.TryExecuteAsync(() =>
        {
            Interlocked.Increment(ref rejectedExecutionCount);
            return Task.CompletedTask;
        });

        Assert.Equal(LegacyCommandAdmissionResult.RejectedCapacity, rejected);
        Assert.Equal(0, rejectedExecutionCount);
        Assert.Equal(2, coordinator.ActiveExecutionCount);

        release.TrySetResult();
        var completed = await Task.WhenAll(first, second);
        Assert.All(
            completed,
            result => Assert.Equal(LegacyCommandAdmissionResult.Executed, result));
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task TryExecuteAsync_FailureAndCancellation_ReleaseCapacityExactlyOnce()
    {
        var coordinator = new LegacyCommandExecutionCoordinator(
            maximumConcurrentExecutions: 1,
            drainTimeout: TimeSpan.FromSeconds(1));
        var expectedFailure = new InvalidOperationException("injected command failure");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.TryExecuteAsync(() => Task.FromException(expectedFailure)));
        Assert.Same(expectedFailure, failure);
        Assert.Equal(0, coordinator.ActiveExecutionCount);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.TryExecuteAsync(() => Task.FromCanceled(cancellation.Token)));
        Assert.Equal(0, coordinator.ActiveExecutionCount);

        var reused = await coordinator.TryExecuteAsync(() => Task.CompletedTask);
        Assert.Equal(LegacyCommandAdmissionResult.Executed, reused);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task DrainAsync_StopsAdmissionAndWaitsForAlreadyAdmittedExecution()
    {
        var coordinator = new LegacyCommandExecutionCoordinator(
            maximumConcurrentExecutions: 2,
            drainTimeout: TimeSpan.FromSeconds(1));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var admitted = coordinator.TryExecuteAsync(async () =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        coordinator.StopAdmission();
        var rejected = await coordinator.TryExecuteAsync(() => Task.CompletedTask);
        Assert.Equal(LegacyCommandAdmissionResult.RejectedStopping, rejected);

        var drain = coordinator.DrainAsync(_ => { });
        Assert.False(drain.IsCompleted);

        release.TrySetResult();
        var drainResult = await drain.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(drainResult.IsDrained);
        Assert.Equal(0, drainResult.SurvivingExecutionCount);
        Assert.Equal(LegacyCommandAdmissionResult.Executed, await admitted);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task DrainAsync_TimeoutReturnsSurvivorAndObservesLateFault()
    {
        var coordinator = new LegacyCommandExecutionCoordinator(
            maximumConcurrentExecutions: 1,
            drainTimeout: TimeSpan.FromMilliseconds(50));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var commandCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFailure = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var admitted = coordinator.TryExecuteAsync(async () =>
        {
            started.TrySetResult();
            await commandCompletion.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        coordinator.StopAdmission();
        var drainResult = await coordinator.DrainAsync(
            exception => lateFailure.TrySetResult(exception));

        Assert.False(drainResult.IsDrained);
        Assert.Equal(1, drainResult.SurvivingExecutionCount);
        Assert.Equal(1, coordinator.ActiveExecutionCount);

        var expectedFailure = new InvalidOperationException("late command failure");
        commandCompletion.TrySetException(expectedFailure);

        var observedFailure = await lateFailure.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Same(expectedFailure, observedFailure);
        var commandFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => admitted);
        Assert.Same(expectedFailure, commandFailure);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task StopAdmission_IsIdempotentAndPreventsFutureExecution()
    {
        var coordinator = new LegacyCommandExecutionCoordinator(
            maximumConcurrentExecutions: 1,
            drainTimeout: TimeSpan.FromSeconds(1));

        coordinator.StopAdmission();
        coordinator.StopAdmission();

        var rejected = await coordinator.TryExecuteAsync(() => Task.CompletedTask);
        var firstDrain = await coordinator.DrainAsync(_ => { });
        var secondDrain = await coordinator.DrainAsync(_ => { });

        Assert.Equal(LegacyCommandAdmissionResult.RejectedStopping, rejected);
        Assert.True(firstDrain.IsDrained);
        Assert.True(secondDrain.IsDrained);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }
}
