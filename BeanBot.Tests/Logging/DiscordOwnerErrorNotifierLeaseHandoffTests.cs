using BeanBot.Logging;
using Xunit;

namespace BeanBot.Tests.Logging;

public class DiscordOwnerErrorNotifierLeaseHandoffTests
{
    [Fact]
    public async Task DisposeAsync_UnresponsiveDeliveryRemainsOwnedAndRejectsNewAlerts()
    {
        var deliveryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new BlockingDelivery(deliveryStarted, deliveryCompletion);
        await using var notifier = new DiscordOwnerErrorNotifier(
            delivery,
            _ => TimeSpan.Zero,
            TimeSpan.FromMilliseconds(25));

        notifier.Enqueue("first");
        await deliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(notifier.HasActiveDiscordOperation);

        await notifier.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(notifier.HasActiveDiscordOperation);
        notifier.Enqueue("second");
        Assert.Equal(1, delivery.CallCount);

        deliveryCompletion.TrySetResult();
        await deliveryCompletion.Task;
    }

    private sealed class BlockingDelivery : IOwnerAlertDelivery
    {
        private readonly TaskCompletionSource _started;
        private readonly TaskCompletionSource _completion;
        private int _callCount;

        public BlockingDelivery(
            TaskCompletionSource started,
            TaskCompletionSource completion)
        {
            _started = started;
            _completion = completion;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult();
            return _completion.Task;
        }
    }
}
