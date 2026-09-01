using BeanBot.Discord.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Messaging;

public class PaginatorDiscordOperationTrackerTests
{
    [Fact]
    public async Task RunAsync_SuccessExecutesOnceAndReclaimsOwnership()
    {
        var tracker = CreateTracker(maximumOperations: 1);
        var calls = 0;
        CancellationToken requestToken = default;

        var result = await tracker.RunAsync(
            "send message",
            options =>
            {
                calls++;
                requestToken = options.CancelToken;
                return Task.FromResult(42);
            });

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
        Assert.True(requestToken.CanBeCanceled);
        Assert.False(requestToken.IsCancellationRequested);
        Assert.True(SpinWait.SpinUntil(
            () => tracker.OwnedOperationCount == 0,
            TimeSpan.FromSeconds(1)));

        await tracker.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RunAsync_TimeoutCancelsSharedRequestTokenAndObservesLateFailure()
    {
        var lateFailure = new TaskCompletionSource<(Exception Exception, string Operation)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = CreateTracker(
            maximumOperations: 1,
            operationTimeout: TimeSpan.FromMilliseconds(25),
            lateFailureObserver: (exception, operation) =>
                lateFailure.TrySetResult((exception, operation)));
        var operation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken requestToken = default;

        await Assert.ThrowsAsync<TimeoutException>(() => tracker.RunAsync(
            "modify page",
            options =>
            {
                calls++;
                requestToken = options.CancelToken;
                return operation.Task;
            }));

        Assert.Equal(1, calls);
        Assert.True(requestToken.IsCancellationRequested);
        Assert.Equal(1, tracker.OwnedOperationCount);

        var lateException = new InvalidOperationException("late paginator failure");
        operation.SetException(lateException);
        var observed = await lateFailure.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Same(lateException, observed.Exception);
        Assert.Equal("modify page", observed.Operation);
        Assert.True(SpinWait.SpinUntil(
            () => tracker.OwnedOperationCount == 0,
            TimeSpan.FromSeconds(1)));

        await tracker.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RunAsync_RepeatedTimeoutsCannotExceedHardOperationBound()
    {
        var tracker = CreateTracker(
            maximumOperations: 2,
            operationTimeout: TimeSpan.FromMilliseconds(25));
        var first = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            tracker.RunAsync("first operation", _ => first.Task));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            tracker.RunAsync("second operation", _ => second.Task));

        Assert.Equal(2, tracker.OwnedOperationCount);

        var thirdCalls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tracker.RunAsync(
                "third operation",
                _ =>
                {
                    thirdCalls++;
                    return Task.CompletedTask;
                }));
        Assert.Equal(0, thirdCalls);
        Assert.Equal(2, tracker.OwnedOperationCount);

        first.SetResult();
        Assert.True(SpinWait.SpinUntil(
            () => tracker.OwnedOperationCount == 1,
            TimeSpan.FromSeconds(1)));

        await tracker.RunAsync("replacement operation", _ => Task.CompletedTask);
        Assert.True(SpinWait.SpinUntil(
            () => tracker.OwnedOperationCount == 1,
            TimeSpan.FromSeconds(1)));

        second.SetResult();
        await tracker.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, tracker.OwnedOperationCount);
    }

    [Fact]
    public async Task RunAsync_ShutdownCancellationIsNotReportedAsTimeout()
    {
        using var shutdown = new CancellationTokenSource();
        var tracker = CreateTracker(
            maximumOperations: 1,
            operationTimeout: TimeSpan.FromSeconds(1),
            shutdownCancellation: shutdown.Token);
        var operation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;

        var run = tracker.RunAsync(
            "remove expired control",
            options =>
            {
                requestToken = options.CancelToken;
                return operation.Task;
            });
        shutdown.Cancel();

        var exception = await Record.ExceptionAsync(async () => await run);

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.True(requestToken.IsCancellationRequested);
        Assert.Equal(1, tracker.OwnedOperationCount);

        var firstStop = tracker.StopAsync();
        var repeatedStop = tracker.StopAsync();
        Assert.Same(firstStop, repeatedStop);
        Assert.False(firstStop.IsCompleted);

        operation.SetResult();
        await firstStop.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, tracker.OwnedOperationCount);
    }

    private static PaginatorDiscordOperationTracker CreateTracker(
        int maximumOperations,
        TimeSpan? operationTimeout = null,
        CancellationToken shutdownCancellation = default,
        Action<Exception, string>? lateFailureObserver = null)
        => new(
            maximumOperations,
            operationTimeout ?? TimeSpan.FromSeconds(1),
            shutdownCancellation,
            NullLogger<DiscordPaginatorService>.Instance,
            lateFailureObserver);
}
