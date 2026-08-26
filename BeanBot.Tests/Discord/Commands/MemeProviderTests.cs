using BeanBot.Discord.Commands;
using MemeApiDotNetWrapper;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class MemeProviderTests
{
    [Fact]
    public async Task GetMemeAsync_Success_ForwardsSubreddit()
    {
        string? receivedSubreddit = null;
        var provider = new MemeProvider(
            subreddit =>
            {
                receivedSubreddit = subreddit;
                return Task.FromResult<Meme?>(null);
            },
            TimeSpan.FromSeconds(1));

        var result = await provider.GetMemeAsync("memes", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal("memes", receivedSubreddit);
    }

    [Fact]
    public async Task GetMemeAsync_StalledRequest_TimesOutWithoutRetry()
    {
        var calls = 0;
        var completion = new TaskCompletionSource<Meme?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new MemeProvider(
            _ =>
            {
                calls++;
                return completion.Task;
            },
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            provider.GetMemeAsync(string.Empty, CancellationToken.None));

        Assert.Equal(1, calls);
        completion.SetException(new InvalidOperationException("late API failure"));
        await Task.Yield();
    }

    [Fact]
    public async Task GetMemeAsync_CallerCancellation_IsNotReportedAsTimeout()
    {
        var completion = new TaskCompletionSource<Meme?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new MemeProvider(_ => completion.Task, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        var request = provider.GetMemeAsync(string.Empty, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        completion.SetException(new InvalidOperationException("late API failure"));
        await Task.Yield();
    }
}
