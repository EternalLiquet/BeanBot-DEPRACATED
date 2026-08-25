using BeanBot.Discord.Commands;
using Discord;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class BoundedDiscordFileSenderTests
{
    [Fact]
    public async Task SendAsync_Success_SendsContentOnce()
    {
        var calls = 0;
        byte[]? sentContent = null;

        await BoundedDiscordFileSender.SendAsync(
            async (stream, requestOptions) =>
            {
                calls++;
                Assert.False(requestOptions.CancelToken.IsCancellationRequested);
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                sentContent = copy.ToArray();
            },
            [1, 2, 3],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal([1, 2, 3], sentContent);
    }

    [Fact]
    public async Task SendAsync_StalledUpload_TimesOutWithoutRetryAndCancelsRequest()
    {
        var calls = 0;
        CancellationToken requestToken = default;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() => BoundedDiscordFileSender.SendAsync(
            (_, requestOptions) =>
            {
                calls++;
                requestToken = requestOptions.CancelToken;
                return completion.Task;
            },
            [1],
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.True(requestToken.IsCancellationRequested);

        completion.SetException(new InvalidOperationException("late upload failure"));
        await Task.Yield();
    }

    [Fact]
    public async Task SendAsync_CallerCancellation_IsNotReportedAsTimeout()
    {
        var calls = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var send = BoundedDiscordFileSender.SendAsync(
            (_, _) =>
            {
                calls++;
                return completion.Task;
            },
            [1],
            TimeSpan.FromSeconds(5),
            cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Equal(1, calls);

        completion.SetException(new InvalidOperationException("late upload failure"));
        await Task.Yield();
    }
}
