using BeanBot.Util;
using Xunit;

namespace BeanBot.Tests.Util;

public class FortuneAnswerQueueTests
{
    [Fact]
    public void Reservation_CanBeConsumedOnce()
    {
        var queue = new FortuneAnswerQueue();
        queue.Queue(42, positive: true);

        Assert.True(queue.TryReserve(42, out var reservation));
        Assert.Equal("positive", reservation.Answer);
        Assert.True(queue.Consume(reservation));
        Assert.False(queue.TryReserve(42, out _));
    }

    [Fact]
    public void WrongRecipient_DoesNotReserveOrConsumeAnswer()
    {
        var queue = new FortuneAnswerQueue();
        queue.Queue(42, positive: false);

        Assert.False(queue.TryReserve(7, out _));
        Assert.True(queue.TryReserve(42, out var reservation));
        Assert.Equal("negative", reservation.Answer);
    }

    [Fact]
    public void ReplacedAnswer_IsNotConsumedByAnOlderReservation()
    {
        var queue = new FortuneAnswerQueue();
        queue.Queue(42, positive: true);
        Assert.True(queue.TryReserve(42, out var oldReservation));

        queue.Queue(42, positive: true);

        Assert.False(queue.Consume(oldReservation));
        Assert.True(queue.TryReserve(42, out var currentReservation));
        Assert.True(queue.Consume(currentReservation));
    }
}
