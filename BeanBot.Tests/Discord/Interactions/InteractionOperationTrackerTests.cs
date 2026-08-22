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
}
