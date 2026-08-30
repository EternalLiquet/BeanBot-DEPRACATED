using BeanBot.Discord.Events;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Events;

public class EditMessageEventServicesTests
{
    [Theory]
    [InlineData("%8ball should I shower?")]
    [InlineData("%fortune should I shower?")]
    [InlineData("  %FORTUNE question")]
    [InlineData("succ 8ball question")]
    [InlineData("SuCc fortune question")]
    public void IsFortuneCommand_AcceptsSupportedPrefixesAndAliases(string content)
    {
        Assert.True(EditMessageEventServices.IsFortuneCommand(content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("%8ballistic question")]
    [InlineData("fortune question")]
    [InlineData("%pun")]
    [InlineData("succ fortune-cookie")]
    public void IsFortuneCommand_RejectsOtherMessages(string content)
    {
        Assert.False(EditMessageEventServices.IsFortuneCommand(content));
    }

    [Theory]
    [InlineData("<@123> 8ball question")]
    [InlineData("<@!123> fortune question")]
    public void IsFortuneCommand_AcceptsBeanBotMentionPrefix(string content)
    {
        Assert.True(EditMessageEventServices.IsFortuneCommand(content, 123));
    }

    [Fact]
    public void IsFortuneCommand_RejectsAnotherUsersMentionPrefix()
    {
        Assert.False(EditMessageEventServices.IsFortuneCommand("<@456> fortune question", 123));
    }

    [Fact]
    public async Task ReplaceResponseAsync_Success_PreservesWarningAndUsesCancelableRequest()
    {
        string? content = null;
        CancellationToken requestToken = default;

        await EditMessageEventServices.ReplaceResponseAsync(
            (replacement, options) =>
            {
                content = replacement;
                requestToken = options.CancelToken;
                return Task.CompletedTask;
            },
            42,
            NullLogger.Instance,
            CancellationToken.None,
            TimeSpan.FromSeconds(1));

        Assert.Equal(EditMessageEventServices.EditWarning, content);
        Assert.True(requestToken.CanBeCanceled);
        Assert.False(requestToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ReplaceResponseAsync_OrdinaryFailures_RetriesWithBoundedDelays()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();

        await EditMessageEventServices.ReplaceResponseAsync(
            (_, _) =>
            {
                calls++;
                return calls < 3
                    ? Task.FromException(new InvalidOperationException("retryable failure"))
                    : Task.CompletedTask;
            },
            42,
            NullLogger.Instance,
            CancellationToken.None,
            TimeSpan.FromSeconds(1),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal(3, calls);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)],
            delays);
    }

    [Fact]
    public async Task ReplaceResponseAsync_StalledModification_TimesOutWithoutRetryAndTracksLateTask()
    {
        var calls = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? trackedTask = null;
        CancellationToken requestToken = default;

        await EditMessageEventServices.ReplaceResponseAsync(
            (_, options) =>
            {
                calls++;
                requestToken = options.CancelToken;
                return completion.Task;
            },
            42,
            NullLogger.Instance,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(25),
            trackLateDiscordOperation: task => trackedTask = task);

        Assert.Equal(1, calls);
        Assert.Same(completion.Task, trackedTask);
        Assert.True(requestToken.IsCancellationRequested);

        completion.SetException(new InvalidOperationException("late modify failure"));
        await Task.Yield();
    }

    [Fact]
    public async Task ReplaceResponseAsync_ShutdownCancellation_StopsRetryDelay()
    {
        var calls = 0;
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var operation = EditMessageEventServices.ReplaceResponseAsync(
            (_, _) =>
            {
                calls++;
                return Task.FromException(new InvalidOperationException("retryable failure"));
            },
            42,
            NullLogger.Instance,
            cancellation.Token,
            TimeSpan.FromSeconds(1),
            async (_, token) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        await delayStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ResolvePreviousMessageAsync_StalledLookup_TimesOutAndTracksLateTask()
    {
        var completion = new TaskCompletionSource<IMessage?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? trackedTask = null;
        CancellationToken requestToken = default;

        await Assert.ThrowsAsync<TimeoutException>(
            () => EditMessageEventServices.ResolvePreviousMessageAsync(
                options =>
                {
                    requestToken = options.CancelToken;
                    return completion.Task;
                },
                CancellationToken.None,
                TimeSpan.FromMilliseconds(25),
                task => trackedTask = task));

        Assert.Same(completion.Task, trackedTask);
        Assert.True(requestToken.IsCancellationRequested);

        completion.SetException(new InvalidOperationException("late lookup failure"));
        await Task.Yield();
    }
}
