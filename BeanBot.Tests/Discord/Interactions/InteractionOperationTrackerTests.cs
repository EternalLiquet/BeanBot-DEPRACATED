using BeanBot.Discord.Interactions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class InteractionOperationTrackerTests
{
    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsCooperativeOperation()
    {
        var tracker = new InteractionOperationTracker(
            TimeSpan.FromSeconds(1),
            NullLogger.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = tracker.TrackAsync(async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.SetResult();
                throw;
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await tracker.DisposeAsync();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task DisposeAsync_ReturnsWithinBoundForUncooperativeOperation()
    {
        var tracker = new InteractionOperationTracker(
            TimeSpan.FromMilliseconds(25),
            NullLogger.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = tracker.TrackAsync(_ => release.Task);

        await tracker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        release.SetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallsShareTheSameDrain()
    {
        var tracker = new InteractionOperationTracker(
            TimeSpan.FromSeconds(1),
            NullLogger.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = tracker.TrackAsync(_ =>
        {
            started.SetResult();
            return release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var firstDispose = tracker.DisposeAsync().AsTask();
        var secondDispose = tracker.DisposeAsync().AsTask();

        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);
        release.SetResult();
        await Task.WhenAll(firstDispose, secondDispose)
            .WaitAsync(TimeSpan.FromSeconds(1));
        await operation.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DisposeAsync_FaultedDrainCompletesAndPreservesTrackedFailure()
    {
        var tracker = new InteractionOperationTracker(
            TimeSpan.FromSeconds(1),
            NullLogger.Instance);
        var operationSource = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("operation failed during drain");
        var operation = tracker.TrackAsync(_ => operationSource.Task);

        var dispose = tracker.DisposeAsync().AsTask();
        operationSource.SetException(expected);

        await dispose.WaitAsync(TimeSpan.FromSeconds(1));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => operation);
        Assert.Same(expected, thrown);
    }

    [Fact]
    public async Task TrackAsync_AfterDispose_DoesNotStartOperation()
    {
        var tracker = new InteractionOperationTracker(
            TimeSpan.FromSeconds(1),
            NullLogger.Instance);
        await tracker.DisposeAsync();
        var invoked = false;

        await tracker.TrackAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
    }

    [Fact]
    public async Task Start_RunsOperationWithoutMakingCallerAwaitItsCompletion()
    {
        var tracker = new InteractionOperationTracker(
            TimeSpan.FromSeconds(1),
            NullLogger.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var admission = tracker.Start(_ =>
        {
            started.SetResult();
            return release.Task;
        });

        Assert.Equal(InteractionOperationAdmission.Started, admission);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(release.Task.IsCompleted);
        release.SetResult();
        await tracker.DisposeAsync();
    }

    [Fact]
    public async Task Start_AtCapacityRejectsWithoutCreatingAnUnboundedWaiter()
    {
        var tracker = new InteractionOperationTracker(
            TimeSpan.FromSeconds(1),
            NullLogger.Instance,
            maximumOperations: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(
            InteractionOperationAdmission.Started,
            tracker.Start(_ => release.Task));
        var secondInvoked = false;

        var admission = tracker.Start(_ =>
        {
            secondInvoked = true;
            return Task.CompletedTask;
        });

        Assert.Equal(InteractionOperationAdmission.Saturated, admission);
        Assert.False(secondInvoked);
        release.SetResult();
        await tracker.DisposeAsync();
    }

    [Fact]
    public async Task Start_ObservesDetachedSynchronousFailure()
    {
        var tracker = new InteractionOperationTracker(
            TimeSpan.FromSeconds(1),
            NullLogger.Instance);
        var expected = new InvalidOperationException("detached operation failed");

        var admission = tracker.Start(_ => Task.FromException(expected));

        Assert.Equal(InteractionOperationAdmission.Started, admission);
        await tracker.DisposeAsync();
    }
}
