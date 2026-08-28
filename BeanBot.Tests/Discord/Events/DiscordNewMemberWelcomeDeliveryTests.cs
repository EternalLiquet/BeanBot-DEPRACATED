using BeanBot.Configuration;
using BeanBot.Discord.Events;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Events;

public class DiscordNewMemberWelcomeDeliveryTests
{
    [Fact]
    public async Task DeliverAsync_CreatesDmAndSendsConfiguredMessageOnce()
    {
        RequestOptions? createOptions = null;
        RequestOptions? sendOptions = null;
        string? sentMessage = null;
        var delivery = CreateDelivery(
            (userId, options) =>
            {
                Assert.Equal((ulong)42, userId);
                createOptions = options;
                return Task.FromResult<IDMChannel>(null!);
            },
            (_, message, options) =>
            {
                sentMessage = message;
                sendOptions = options;
                return Task.CompletedTask;
            });

        await delivery.DeliverAsync(42, CancellationToken.None);

        Assert.Equal("configured welcome", sentMessage);
        Assert.NotNull(createOptions);
        Assert.Same(createOptions, sendOptions);
        Assert.False(delivery.HasActiveOperation);
    }

    [Fact]
    public async Task DeliverAsync_CreateDmTimeout_DoesNotAttemptSendAndObservesLateFailure()
    {
        var stalled = new TaskCompletionSource<IDMChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCalls = 0;
        var delivery = CreateDelivery(
            (_, _) => stalled.Task,
            (_, _, _) =>
            {
                Interlocked.Increment(ref sendCalls);
                return Task.CompletedTask;
            },
            operationTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => delivery.DeliverAsync(1, CancellationToken.None));

        Assert.Equal(0, sendCalls);
        Assert.True(delivery.HasActiveOperation);
        stalled.SetException(new InvalidOperationException("late create failure"));
        await WaitUntilAsync(() => !delivery.HasActiveOperation);
    }

    [Fact]
    public async Task DeliverAsync_SendTimeout_IsNotRetriedAndLateCompletionReleasesCapacity()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCalls = 0;
        var delivery = CreateDelivery(
            (_, _) => Task.FromResult<IDMChannel>(null!),
            (_, _, _) =>
            {
                Interlocked.Increment(ref sendCalls);
                return stalled.Task;
            },
            operationTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => delivery.DeliverAsync(1, CancellationToken.None));

        Assert.Equal(1, sendCalls);
        Assert.True(delivery.HasActiveOperation);
        stalled.SetResult();
        await WaitUntilAsync(() => !delivery.HasActiveOperation);
        Assert.Equal(1, sendCalls);
    }

    [Fact]
    public async Task DeliverAsync_ShutdownCancellationFlowsToDiscordRequestAndBoundedWait()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new TaskCompletionSource<IDMChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var delivery = CreateDelivery(
            (_, options) =>
            {
                observedToken = options.CancelToken;
                started.TrySetResult();
                return stalled.Task;
            },
            (_, _, _) => Task.CompletedTask,
            operationTimeout: TimeSpan.FromSeconds(5));

        var operation = delivery.DeliverAsync(1, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellation.Token, observedToken);
        Assert.True(delivery.HasActiveOperation);
        stalled.SetCanceled();
        await WaitUntilAsync(() => !delivery.HasActiveOperation);
    }

    [Fact]
    public async Task DeliverAsync_AbandonedDiscordOperationsRemainHardBounded()
    {
        var first = new TaskCompletionSource<IDMChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var runtimeOptions = RuntimeOptions(
            operationTimeout: TimeSpan.FromMilliseconds(25),
            maximumDiscordOperations: 1);
        var delivery = CreateDelivery(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return first.Task;
            },
            (_, _, _) => Task.CompletedTask,
            runtimeOptions: runtimeOptions);

        await Assert.ThrowsAsync<TimeoutException>(() => delivery.DeliverAsync(1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.DeliverAsync(2, CancellationToken.None));
        Assert.Equal(1, calls);

        first.SetResult(null!);
        await WaitUntilAsync(() => !delivery.HasActiveOperation);
    }

    private static DiscordNewMemberWelcomeDelivery CreateDelivery(
        Func<ulong, RequestOptions, Task<IDMChannel>> createDm,
        Func<IDMChannel, string, RequestOptions, Task> sendMessage,
        TimeSpan? operationTimeout = null,
        NewMemberWelcomeRuntimeOptions? runtimeOptions = null)
        => new(
            createDm,
            sendMessage,
            new NewMemberWelcomeOptions(true, "configured welcome"),
            runtimeOptions ?? RuntimeOptions(operationTimeout: operationTimeout),
            NullLogger<DiscordNewMemberWelcomeDelivery>.Instance);

    private static NewMemberWelcomeRuntimeOptions RuntimeOptions(
        TimeSpan? operationTimeout = null,
        int maximumDiscordOperations = 4)
        => new(
            MaximumOutstanding: 8,
            WorkerCount: 2,
            MaximumDiscordOperations: maximumDiscordOperations,
            OperationTimeout: operationTimeout ?? TimeSpan.FromSeconds(1),
            ShutdownDrainTimeout: TimeSpan.FromSeconds(1),
            ShutdownCancellationGrace: TimeSpan.FromSeconds(1));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met before the test deadline.");
            }

            await Task.Delay(10);
        }
    }
}
