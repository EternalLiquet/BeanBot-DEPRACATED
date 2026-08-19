using BeanBot.Discord.Messaging;

using Xunit;

namespace BeanBot.Tests.Discord.Messaging;

public class DiscordMessageWaiterTests
{
    [Fact]
    public async Task MatchingMessage_CompletesWait()
    {
        using var waiter = new BoundedMessageWaiter<string>(2);
        var wait = waiter.WaitAsync(10, 20, TimeSpan.FromSeconds(1));

        Assert.True(waiter.TryPublish(10, 20, isBot: false, "matched"));

        Assert.Equal("matched", await wait);
        Assert.Equal(0, waiter.PendingCount);
    }

    [Theory]
    [InlineData(11UL, 20UL, false)]
    [InlineData(10UL, 21UL, false)]
    [InlineData(10UL, 20UL, true)]
    public async Task UnrelatedOrBotMessage_DoesNotCompleteWait(
        ulong userId,
        ulong channelId,
        bool isBot)
    {
        using var waiter = new BoundedMessageWaiter<string>(2);
        var wait = waiter.WaitAsync(10, 20, TimeSpan.FromMilliseconds(50));

        Assert.False(waiter.TryPublish(userId, channelId, isBot, "ignored"));

        Assert.Null(await wait);
    }

    [Fact]
    public async Task Timeout_ReturnsNullAndReleasesSlot()
    {
        using var waiter = new BoundedMessageWaiter<string>(1);

        Assert.Null(await waiter.WaitAsync(10, 20, TimeSpan.FromMilliseconds(20)));
        Assert.Equal(0, waiter.PendingCount);
        Assert.Null(await waiter.WaitAsync(11, 21, TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task Cancellation_PropagatesAndReleasesSlot()
    {
        using var waiter = new BoundedMessageWaiter<string>(1);
        using var cancellation = new CancellationTokenSource();
        var wait = waiter.WaitAsync(10, 20, TimeSpan.FromMinutes(1), cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(0, waiter.PendingCount);
    }

    [Fact]
    public async Task DuplicateUserAndChannelWait_IsRejected()
    {
        using var waiter = new BoundedMessageWaiter<string>(2);
        var first = waiter.WaitAsync(10, 20, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => waiter.WaitAsync(10, 20, TimeSpan.FromSeconds(1)));

        Assert.True(waiter.TryPublish(10, 20, isBot: false, "done"));
        Assert.Equal("done", await first);
    }

    [Fact]
    public async Task Capacity_IsBounded()
    {
        using var waiter = new BoundedMessageWaiter<string>(1);
        var first = waiter.WaitAsync(10, 20, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => waiter.WaitAsync(11, 21, TimeSpan.FromSeconds(1)));

        Assert.True(waiter.TryPublish(10, 20, isBot: false, "done"));
        Assert.Equal("done", await first);
    }

    [Fact]
    public async Task Dispose_FailsPendingWaitAndRejectsNewWaits()
    {
        var waiter = new BoundedMessageWaiter<string>(1);
        var pending = waiter.WaitAsync(10, 20, TimeSpan.FromMinutes(1));

        waiter.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => waiter.WaitAsync(11, 21, TimeSpan.FromSeconds(1)));
    }
}
