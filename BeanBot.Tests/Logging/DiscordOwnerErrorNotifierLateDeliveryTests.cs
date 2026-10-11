using BeanBot.Logging;
using Xunit;

namespace BeanBot.Tests.Logging;

public class DiscordOwnerErrorNotifierLateDeliveryTests
{
    [Fact]
    public async Task DisposeAsync_BoundsShutdownButRetainsOwnershipOfUnfinishedDm()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new BlockingDelivery(started, completion);
        await using var notifier = new DiscordOwnerErrorNotifier(
            delivery,
            _ => TimeSpan.Zero,
            TimeSpan.FromMilliseconds(25));

        notifier.Enqueue("startup report");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(notifier.HasActiveDiscordOperation);

        await notifier.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(notifier.HasActiveDiscordOperation);
        notifier.Enqueue("late alert");
        Assert.Equal(1, delivery.CallCount);

        completion.TrySetResult();
        Assert.False(notifier.HasActiveDiscordOperation);
    }

    private sealed class BlockingDelivery(
        TaskCompletionSource started,
        TaskCompletionSource completion) : IOwnerAlertDelivery
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            started.TrySetResult();
            return completion.Task;
        }
    }
}
