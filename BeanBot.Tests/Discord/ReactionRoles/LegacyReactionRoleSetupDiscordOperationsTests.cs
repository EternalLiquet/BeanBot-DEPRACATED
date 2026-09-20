using BeanBot.Discord.ReactionRoles;
using Discord;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class LegacyReactionRoleSetupDiscordOperationsTests
{
    [Fact]
    public async Task SetupSuccess_AddsEveryControlOnceBeforePersistence()
    {
        var logger = new RecordingLogger<LegacyReactionRoleSetupDiscordOperations>();
        var operations = CreateOperations(logger);
        var calls = new List<string>();

        var result = await ReactionRoleSetupTransaction.ExecuteAsync(
            () => Task.FromResult("panel"),
            async _ =>
            {
                await operations.AddReactionsAsync(
                    new[] { 1, 2, 3 },
                    (control, _) =>
                    {
                        calls.Add($"reaction:{control}");
                        return Task.CompletedTask;
                    });
                calls.Add("persist");
            },
            _ => throw new InvalidOperationException("Successful setup must not compensate."),
            _ => throw new InvalidOperationException("Successful setup must not report compensation failure."));

        Assert.Equal("panel", result);
        Assert.Equal(new[] { "reaction:1", "reaction:2", "reaction:3", "persist" }, calls);
        Assert.False(operations.HasPendingOperations);
    }

    [Fact]
    public async Task AddReactionsAsync_DefiniteFailureStopsFollowingControls()
    {
        var logger = new RecordingLogger<LegacyReactionRoleSetupDiscordOperations>();
        var operations = CreateOperations(logger);
        var attempts = new List<int>();
        var failure = new InvalidOperationException("Discord rejected reaction");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operations.AddReactionsAsync(
                new[] { 1, 2, 3 },
                (control, _) =>
                {
                    attempts.Add(control);
                    return control == 2
                        ? Task.FromException(failure)
                        : Task.CompletedTask;
                }));

        Assert.Same(failure, actual);
        Assert.Equal(new[] { 1, 2 }, attempts);
        Assert.False(operations.HasPendingOperations);
    }

    [Fact]
    public async Task RunOperationAsync_TimeoutDoesNotRetryAndRetainsBoundedOwnershipUntilLateFault()
    {
        var logger = new RecordingLogger<LegacyReactionRoleSetupDiscordOperations>();
        CancellationTokenSource? timeoutSource = null;
        CancellationToken timeoutToken = default;
        var operations = CreateOperations(
            logger,
            capacity: 1,
            scheduleTimeout: (source, _) =>
            {
                timeoutSource = source;
                timeoutToken = source.Token;
            });
        var discordOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        CancellationToken requestToken = default;

        var wait = operations.RunOperationAsync(
            options =>
            {
                attempts++;
                requestToken = options.CancelToken;
                return discordOperation.Task;
            },
            "reaction-add");

        Assert.NotNull(timeoutSource);
        timeoutSource!.Cancel();
        await Assert.ThrowsAsync<LegacyReactionRoleSetupOperationTimeoutException>(() => wait);

        Assert.Equal(1, attempts);
        Assert.Equal(timeoutToken, requestToken);
        Assert.True(requestToken.IsCancellationRequested);
        Assert.Equal(1, operations.OwnedOperationCount);

        await Assert.ThrowsAsync<LegacyReactionRoleSetupOperationRejectedException>(() =>
            operations.RunOperationAsync(
                _ =>
                {
                    attempts++;
                    return Task.CompletedTask;
                },
                "reaction-add"));
        Assert.Equal(1, attempts);

        var lateFailure = new InvalidOperationException("late Discord failure");
        discordOperation.SetException(lateFailure);

        Assert.Equal(0, operations.OwnedOperationCount);
        var logEntry = Assert.Single(
            logger.Entries,
            candidate => candidate.Level == LogLevel.Error && candidate.Exception is AggregateException);
        Assert.Contains(lateFailure, ((AggregateException)logEntry.Exception!).InnerExceptions);
    }

    [Fact]
    public async Task RunOperationAsync_ApplicationCancellationIsNotMisclassifiedAsTimeout()
    {
        using var applicationStopping = new CancellationTokenSource();
        var logger = new RecordingLogger<LegacyReactionRoleSetupDiscordOperations>();
        var operations = CreateOperations(
            logger,
            applicationStopping: applicationStopping.Token);
        var discordOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        var attempts = 0;

        var wait = operations.RunOperationAsync(
            options =>
            {
                attempts++;
                requestToken = options.CancelToken;
                return discordOperation.Task;
            },
            "reaction-add");
        applicationStopping.Cancel();

        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.IsNotType<LegacyReactionRoleSetupOperationTimeoutException>(cancellation);
        Assert.True(requestToken.IsCancellationRequested);
        Assert.Equal(1, operations.OwnedOperationCount);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operations.RunOperationAsync(
                _ =>
                {
                    attempts++;
                    return Task.CompletedTask;
                },
                "reaction-add"));
        Assert.Equal(1, attempts);

        discordOperation.SetCanceled(requestToken);
        Assert.False(operations.HasPendingOperations);
    }

    [Fact]
    public async Task AddReactionsAsync_PacingCancellationStopsBeforeNextControl()
    {
        using var callerCancellation = new CancellationTokenSource();
        var logger = new RecordingLogger<LegacyReactionRoleSetupDiscordOperations>();
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken delayToken = default;
        TimeSpan observedDelay = default;
        var operations = CreateOperations(
            logger,
            interReactionDelay: TimeSpan.FromMilliseconds(250),
            delayAsync: (delay, cancellationToken) =>
            {
                observedDelay = delay;
                delayToken = cancellationToken;
                delayStarted.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        var attempts = new List<int>();

        var wait = operations.AddReactionsAsync(
            new[] { 1, 2 },
            (control, _) =>
            {
                attempts.Add(control);
                return Task.CompletedTask;
            },
            callerCancellation.Token);

        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        Assert.Equal(TimeSpan.FromMilliseconds(250), observedDelay);
        Assert.True(delayToken.IsCancellationRequested);
        Assert.Equal(new[] { 1 }, attempts);
        Assert.False(operations.HasPendingOperations);
    }

    [Fact]
    public async Task TimedOutReaction_SuppressesPersistenceStopsLaterControlsAndCompensatesOnce()
    {
        var logger = new RecordingLogger<LegacyReactionRoleSetupDiscordOperations>();
        var timeoutSources = new List<CancellationTokenSource>();
        var operations = CreateOperations(
            logger,
            scheduleTimeout: (source, _) => timeoutSources.Add(source));
        var stalledReaction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<int>();
        var persisted = 0;
        var compensationDeletes = 0;

        var setup = ReactionRoleSetupTransaction.ExecuteAsync(
            () => Task.FromResult("panel"),
            async _ =>
            {
                await operations.AddReactionsAsync(
                    new[] { 1, 2, 3 },
                    (control, _) =>
                    {
                        attempts.Add(control);
                        return control == 2 ? stalledReaction.Task : Task.CompletedTask;
                    });
                persisted++;
            },
            _ =>
            {
                compensationDeletes++;
                return Task.CompletedTask;
            },
            _ => { });

        Assert.Equal(2, timeoutSources.Count);
        timeoutSources[1].Cancel();
        await Assert.ThrowsAsync<LegacyReactionRoleSetupOperationTimeoutException>(() => setup);

        Assert.Equal(new[] { 1, 2 }, attempts);
        Assert.Equal(0, persisted);
        Assert.Equal(1, compensationDeletes);
        Assert.Equal(1, operations.OwnedOperationCount);

        stalledReaction.SetCanceled();
        Assert.False(operations.HasPendingOperations);
    }

    [Fact]
    public async Task TimedOutCompensation_IsReportedWithoutHidingOriginalFailureOrRetryingDelete()
    {
        var logger = new RecordingLogger<LegacyReactionRoleSetupDiscordOperations>();
        CancellationTokenSource? timeoutSource = null;
        var operations = CreateOperations(
            logger,
            scheduleTimeout: (source, _) => timeoutSource = source);
        var originalFailure = new InvalidOperationException("persistence failed");
        var deleteOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reportedCompensation = null;
        var deleteAttempts = 0;

        var setup = ReactionRoleSetupTransaction.ExecuteAsync(
            () => Task.FromResult("panel"),
            _ => Task.FromException(originalFailure),
            _ => operations.RunOperationAsync(
                _ =>
                {
                    deleteAttempts++;
                    return deleteOperation.Task;
                },
                "compensation-delete"),
            exception => reportedCompensation = exception);

        Assert.NotNull(timeoutSource);
        timeoutSource!.Cancel();
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => setup);

        Assert.Same(originalFailure, actual);
        Assert.IsType<LegacyReactionRoleSetupOperationTimeoutException>(reportedCompensation);
        Assert.Equal(1, deleteAttempts);
        Assert.Equal(1, operations.OwnedOperationCount);

        var lateFailure = new InvalidOperationException("late compensation failure");
        deleteOperation.SetException(lateFailure);

        Assert.False(operations.HasPendingOperations);
        var logEntry = Assert.Single(
            logger.Entries,
            candidate => candidate.Level == LogLevel.Error && candidate.Exception is AggregateException);
        Assert.Contains(lateFailure, ((AggregateException)logEntry.Exception!).InnerExceptions);
    }

    [Fact]
    public void Defaults_AreFiniteAndCapacityIsHardBounded()
    {
        Assert.InRange(LegacyReactionRoleSetupDiscordOperations.DefaultCapacity, 1, 64);
        Assert.True(LegacyReactionRoleSetupDiscordOperations.DefaultOperationTimeout > TimeSpan.Zero);
        Assert.True(LegacyReactionRoleSetupDiscordOperations.DefaultOperationTimeout <= TimeSpan.FromSeconds(30));
        Assert.True(LegacyReactionRoleSetupDiscordOperations.DefaultInterReactionDelay >= TimeSpan.Zero);
        Assert.True(LegacyReactionRoleSetupDiscordOperations.DefaultInterReactionDelay <= TimeSpan.FromSeconds(1));
    }

    private static LegacyReactionRoleSetupDiscordOperations CreateOperations(
        RecordingLogger<LegacyReactionRoleSetupDiscordOperations> logger,
        int capacity = 4,
        TimeSpan? operationTimeout = null,
        TimeSpan? interReactionDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<CancellationTokenSource, TimeSpan>? scheduleTimeout = null,
        CancellationToken applicationStopping = default)
        => new(
            logger,
            capacity,
            operationTimeout ?? TimeSpan.FromSeconds(1),
            interReactionDelay ?? TimeSpan.Zero,
            delayAsync ?? NoDelayAsync,
            scheduleTimeout ?? DoNotScheduleTimeout,
            applicationStopping);

    private static Task NoDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        _ = delay;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static void DoNotScheduleTimeout(CancellationTokenSource cancellation, TimeSpan timeout)
    {
        _ = cancellation;
        _ = timeout;
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
