using BeanBot.Discord.Commands;
using Discord;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class LegacyCommandReplySenderTests
{
    [Fact]
    public async Task RunAsync_Success_StartsExactlyOnceAndUsesCancelableRequestToken()
    {
        var logger = new RecordingLogger<LegacyCommandReplySender>();
        var sender = CreateSender(logger);
        var attempts = 0;
        CancellationToken requestToken = default;

        var result = await sender.RunAsync(
            options =>
            {
                attempts++;
                requestToken = options.CancelToken;
                return Task.FromResult("sent");
            },
            "text");

        Assert.Equal("sent", result);
        Assert.Equal(1, attempts);
        Assert.True(requestToken.CanBeCanceled);
        Assert.Equal(0, sender.OwnedOperationCount);
    }

    [Fact]
    public async Task RunAsync_Timeout_DoesNotRetryAndRetainsOwnershipUntilUnderlyingTaskSettles()
    {
        var logger = new RecordingLogger<LegacyCommandReplySender>();
        var sender = CreateSender(
            logger,
            capacity: 1,
            sendTimeout: TimeSpan.FromMilliseconds(20));
        var firstSend = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        CancellationToken requestToken = default;

        await Assert.ThrowsAsync<LegacyCommandReplyTimeoutException>(
            () => sender.RunAsync(
                options =>
                {
                    attempts++;
                    requestToken = options.CancelToken;
                    return firstSend.Task;
                },
                "text"));

        Assert.Equal(1, attempts);
        Assert.True(requestToken.IsCancellationRequested);
        Assert.Equal(1, sender.OwnedOperationCount);

        await Assert.ThrowsAsync<LegacyCommandReplyRejectedException>(
            () => sender.RunAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromResult("unexpected");
                },
                "embed"));
        Assert.Equal(1, attempts);

        firstSend.SetResult("late success");
        await WaitUntilAsync(() => sender.OwnedOperationCount == 0);

        var secondResult = await sender.RunAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult("reused");
            },
            "file");

        Assert.Equal("reused", secondResult);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RunAsync_LateFault_IsObservedLoggedAndReleasesOwnershipOnce()
    {
        var logger = new RecordingLogger<LegacyCommandReplySender>();
        var sender = CreateSender(logger, sendTimeout: TimeSpan.FromMilliseconds(20));
        var send = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<LegacyCommandReplyTimeoutException>(
            () => sender.RunAsync(_ => send.Task, "text"));

        var lateFailure = new InvalidOperationException("late Discord failure");
        send.SetException(lateFailure);
        await WaitUntilAsync(() => sender.OwnedOperationCount == 0);

        var entry = Assert.Single(
            logger.Entries,
            candidate => candidate.Level == LogLevel.Error && candidate.Exception is AggregateException);
        Assert.Contains(lateFailure, ((AggregateException)entry.Exception!).InnerExceptions);
    }

    [Fact]
    public async Task RunAsync_HostCancellation_PropagatesCancellationInsteadOfTimeout()
    {
        using var applicationStopping = new CancellationTokenSource();
        var logger = new RecordingLogger<LegacyCommandReplySender>();
        var sender = CreateSender(
            logger,
            sendTimeout: TimeSpan.FromSeconds(1),
            applicationStopping: applicationStopping.Token);
        var send = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;

        var operation = sender.RunAsync(
            options =>
            {
                requestToken = options.CancelToken;
                return send.Task;
            },
            "text");
        applicationStopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(requestToken.IsCancellationRequested);
        Assert.Equal(1, sender.OwnedOperationCount);

        send.SetCanceled(requestToken);
        await WaitUntilAsync(() => sender.OwnedOperationCount == 0);
    }

    [Fact]
    public async Task StopAsync_RejectsNewRepliesAndKeepsSurvivingDiscordWorkOwnedAfterDrainBound()
    {
        var logger = new RecordingLogger<LegacyCommandReplySender>();
        var sender = CreateSender(
            logger,
            sendTimeout: TimeSpan.FromSeconds(1),
            drainTimeout: TimeSpan.FromMilliseconds(20));
        var send = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;

        var operation = sender.RunAsync(
            options =>
            {
                requestToken = options.CancelToken;
                return send.Task;
            },
            "file");
        await sender.StopAsync();

        Assert.True(requestToken.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(sender.HasPendingOperations);
        await Assert.ThrowsAsync<LegacyCommandReplyRejectedException>(
            () => sender.RunAsync(_ => Task.FromResult("unexpected"), "text"));

        send.SetResult("late success");
        await WaitUntilAsync(() => !sender.HasPendingOperations);
        await sender.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WhenUnderlyingSendHonorsCancellation_DrainsBeforeReturning()
    {
        var logger = new RecordingLogger<LegacyCommandReplySender>();
        var sender = CreateSender(logger, drainTimeout: TimeSpan.FromSeconds(1));
        CancellationToken requestToken = default;

        var operation = sender.RunAsync(
            async options =>
            {
                requestToken = options.CancelToken;
                await Task.Delay(Timeout.InfiniteTimeSpan, requestToken);
                return "never";
            },
            "embed");

        await sender.StopAsync();

        Assert.True(requestToken.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(sender.HasPendingOperations);
    }

    [Fact]
    public void Defaults_AreFiniteAndCapacityIsHardBounded()
    {
        Assert.InRange(LegacyCommandReplySender.DefaultCapacity, 1, 64);
        Assert.True(LegacyCommandReplySender.DefaultSendTimeout > TimeSpan.Zero);
        Assert.True(LegacyCommandReplySender.DefaultSendTimeout <= TimeSpan.FromSeconds(30));
        Assert.True(LegacyCommandReplySender.DefaultDrainTimeout > TimeSpan.Zero);
        Assert.True(LegacyCommandReplySender.DefaultDrainTimeout <= TimeSpan.FromSeconds(15));
    }

    private static LegacyCommandReplySender CreateSender(
        RecordingLogger<LegacyCommandReplySender> logger,
        int capacity = 4,
        TimeSpan? sendTimeout = null,
        TimeSpan? drainTimeout = null,
        CancellationToken applicationStopping = default)
        => new(
            logger,
            capacity,
            sendTimeout ?? TimeSpan.FromSeconds(1),
            drainTimeout ?? TimeSpan.FromSeconds(1),
            applicationStopping);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "Expected condition did not become true in time.");
            await Task.Delay(5);
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, exception));
    }
}
