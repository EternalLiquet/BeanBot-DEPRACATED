using System.Collections.Concurrent;
using BeanBot.Hosting;
using BeanBot.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotStartupReportTests
{
    [Fact]
    public void Format_UsesRunningVersionAndReadableChannelNameWithoutUrlsOrIds()
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.FromCachedChannel("daily-puns", canView: true, canSend: true),
            new Uri("https://user:password@example.test/image?token=private"),
            new Uri("https://example.test/secret-path?key=secret"));

        Assert.Contains("Bean Bot v2.18.3 started.", report);
        Assert.Contains("Daily pun channel: #daily-puns — Ready (View Channel and Send Messages available).", report);
        Assert.Contains("Hatoete image URL: configured (reachability not checked).", report);
        Assert.Contains("Yoshimaru image URL: configured (reachability not checked).", report);
        Assert.DoesNotContain("example.test", report);
        Assert.DoesNotContain("password", report);
        Assert.DoesNotContain("token", report);
        Assert.DoesNotContain("secret", report);
    }

    [Theory]
    [InlineData(false, true, "View Channel")]
    [InlineData(true, false, "Send Messages")]
    [InlineData(false, false, "View Channel and Send Messages")]
    public void Format_NamesMissingPostingPermissions(
        bool canView,
        bool canSend,
        string missingPermissions)
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.FromCachedChannel("daily-puns", canView, canSend),
            null,
            null);

        Assert.Contains($"#daily-puns — missing permissions: {missingPermissions}.", report);
    }

    [Fact]
    public void Format_DistinguishesMissingChannelAndMissingUrls()
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.NotFound,
            null,
            null);

        Assert.Contains("Daily pun channel: not found. Check the channel setting and my access.", report);
        Assert.Contains("Hatoete image URL: missing.", report);
        Assert.Contains("Yoshimaru image URL: missing.", report);
    }

    [Fact]
    public void Format_DoesNotClaimPermissionWhenBotIsUnavailableInCache()
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.FromCachedChannel("daily-puns", null, null),
            null,
            null);

        Assert.Contains("#daily-puns — I couldn't check this channel yet.", report);
        Assert.DoesNotContain("Ready", report);
    }

    [Theory]
    [InlineData("0.0.0-local", "Bean Bot development build started.")]
    [InlineData("unknown", "Bean Bot started (version unavailable).")]
    public void Format_UsesHonestVersionFallback(string version, string expected)
    {
        var report = BeanBotStartupReport.Format(
            version,
            PunChannelStartupStatus.NotFound,
            null,
            null);

        Assert.StartsWith(expected, report);
    }

    [Fact]
    public void Format_SanitizesUntrustedChannelName()
    {
        var report = BeanBotStartupReport.Format(
            "2.18.3",
            PunChannelStartupStatus.FromCachedChannel("daily\n@everyone`puns", true, true),
            null,
            null);

        Assert.DoesNotContain("@everyone", report);
        Assert.DoesNotContain('`', report);
        Assert.DoesNotContain("daily\n", report);
    }

    [Fact]
    public async Task QueueOnFirstReady_IsAtomicAndDoesNotRepeatOnReconnect()
    {
        var delivery = new ScriptedDelivery();
        var checks = 0;
        var outcomes = new OutcomeRecorder();
        var reporter = CreateReporter(
            delivery,
            outcomes,
            new AutoAdvancingClock(),
            () =>
            {
                Interlocked.Increment(ref checks);
                return PunChannelStartupStatus.FromCachedChannel("daily-puns", true, true);
            });

        Parallel.For(0, 20, _ => reporter.QueueOnFirstReady());
        reporter.QueueOnFirstReady();
        await outcomes.Settled.WaitAsync(TestTimeout);
        reporter.QueueOnFirstReady();

        Assert.Equal(1, delivery.CallCount);
        Assert.Equal(1, checks);
        Assert.Equal([false], outcomes.Values);
    }

    [Fact]
    public async Task QueueOnFirstReady_MissingChannelIsHandledOnlyAfterDelivery()
    {
        var delivery = new ScriptedDelivery { BlockFirstSend = true };
        var outcomes = new OutcomeRecorder();
        var reporter = CreateReporter(delivery, outcomes, new NeverFiringClock());

        reporter.QueueOnFirstReady();
        await delivery.FirstSendStarted.Task.WaitAsync(TestTimeout);

        Assert.Empty(outcomes.Values);
        Assert.True(reporter.HasActiveDiscordOperation);

        delivery.ReleaseFirstSend();
        await outcomes.Settled.WaitAsync(TestTimeout);

        Assert.Equal([true], outcomes.Values);
        Assert.Contains("Daily pun channel: not found.", Assert.Single(delivery.Reports));
    }

    [Fact]
    public async Task QueueOnFirstReady_TransientFailuresRetryWithBackoffUntilDelivered()
    {
        var delivery = new ScriptedDelivery { FailuresBeforeSuccess = 3 };
        var outcomes = new OutcomeRecorder();
        var clock = new AutoAdvancingClock();
        var reporter = CreateReporter(delivery, outcomes, clock);

        reporter.QueueOnFirstReady();
        await outcomes.Settled.WaitAsync(TestTimeout);

        Assert.Equal([true], outcomes.Values);
        Assert.Equal(4, delivery.CallCount);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
            clock.RetryDelays);
    }

    [Fact]
    public async Task QueueOnFirstReady_RetriesExpireAfterFiveMinutesAndAllowFallbackAlert()
    {
        var delivery = new ScriptedDelivery { FailuresBeforeSuccess = int.MaxValue };
        var outcomes = new OutcomeRecorder();
        var clock = new AutoAdvancingClock();
        var logger = new RecordingLogger();
        var reporter = CreateReporter(delivery, outcomes, clock, logger: logger);

        reporter.QueueOnFirstReady();
        await outcomes.Settled.WaitAsync(TestTimeout);

        Assert.Equal([false], outcomes.Values);
        Assert.True(delivery.CallCount > 3);
        Assert.All(clock.RetryDelays, delay => Assert.True(delay <= BeanBotStartupReport.MaximumRetryDelay));
        Assert.True(clock.Elapsed <= BeanBotStartupReport.DeliveryWindow);
        Assert.True(
            clock.Elapsed > BeanBotStartupReport.DeliveryWindow - BeanBotStartupReport.MaximumRetryDelay,
            $"Retries stopped early after {clock.Elapsed}.");
        Assert.Contains(LogLevel.Warning, logger.Levels);
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Error);
        Assert.False(reporter.HasActiveDiscordOperation);
    }

    [Fact]
    public async Task QueueOnFirstReady_StalledSendIsNeverOverlappedAndExpiresIntoFallback()
    {
        var delivery = new ScriptedDelivery { BlockFirstSend = true };
        var outcomes = new OutcomeRecorder();
        var clock = new AutoAdvancingClock();
        var reporter = CreateReporter(delivery, outcomes, clock);

        reporter.QueueOnFirstReady();
        await outcomes.Settled.WaitAsync(TestTimeout);

        Assert.Equal([false], outcomes.Values);
        Assert.Equal(1, delivery.CallCount);
        Assert.Equal(BeanBotStartupReport.DeliveryWindow, clock.Elapsed);
        Assert.True(reporter.HasActiveDiscordOperation);

        delivery.ReleaseFirstSend(new InvalidOperationException("late failure"));
        Assert.False(reporter.HasActiveDiscordOperation);
        Assert.Equal(1, delivery.CallCount);
    }

    [Fact]
    public async Task StopAsync_CancelsPendingRetryWithoutReleasingFallbackAlert()
    {
        var delivery = new ScriptedDelivery { FailuresBeforeSuccess = int.MaxValue };
        var outcomes = new OutcomeRecorder();
        var clock = new NeverFiringClock();
        var reporter = CreateReporter(delivery, outcomes, clock);

        reporter.QueueOnFirstReady();
        await clock.TimerCreated.Task.WaitAsync(TestTimeout);
        await reporter.StopAsync().WaitAsync(TestTimeout);

        Assert.Empty(outcomes.Values);
        Assert.Equal(1, delivery.CallCount);
        Assert.False(reporter.HasActiveDiscordOperation);
    }

    [Fact]
    public async Task StopAsync_StalledSendKeepsDiscordOperationActiveWithoutBlockingShutdown()
    {
        var delivery = new ScriptedDelivery { BlockFirstSend = true };
        var outcomes = new OutcomeRecorder();
        var reporter = CreateReporter(delivery, outcomes, new NeverFiringClock());

        reporter.QueueOnFirstReady();
        await delivery.FirstSendStarted.Task.WaitAsync(TestTimeout);
        await reporter.StopAsync().WaitAsync(TestTimeout);

        Assert.Empty(outcomes.Values);
        Assert.True(reporter.HasActiveDiscordOperation);
        delivery.ReleaseFirstSend();
        Assert.False(reporter.HasActiveDiscordOperation);
        Assert.Equal(1, delivery.CallCount);
    }

    [Fact]
    public async Task StopAsync_DuringChannelCheckSendsNothingAndKeepsFallbackSuppressed()
    {
        var delivery = new ScriptedDelivery();
        var outcomes = new OutcomeRecorder();
        Task? stop = null;
        BeanBotStartupReport? reporter = null;
        reporter = CreateReporter(
            delivery,
            outcomes,
            new AutoAdvancingClock(),
            () =>
            {
                stop = reporter!.StopAsync();
                return PunChannelStartupStatus.NotFound;
            });

        reporter.QueueOnFirstReady();
        await stop!.WaitAsync(TestTimeout);

        Assert.Equal(0, delivery.CallCount);
        Assert.Empty(outcomes.Values);
        Assert.False(reporter.HasActiveDiscordOperation);
    }

    [Fact]
    public async Task StopAsync_BeforeReadyPreventsLaterReport()
    {
        var delivery = new ScriptedDelivery();
        var outcomes = new OutcomeRecorder();
        var reporter = CreateReporter(delivery, outcomes, new AutoAdvancingClock());

        await reporter.StopAsync().WaitAsync(TestTimeout);
        reporter.QueueOnFirstReady();

        Assert.Equal(0, delivery.CallCount);
        Assert.Empty(outcomes.Values);
        Assert.False(reporter.HasActiveDiscordOperation);
    }

    [Fact]
    public async Task QueueOnFirstReady_ChannelCheckFailureStillSendsHonestReport()
    {
        var delivery = new ScriptedDelivery();
        var outcomes = new OutcomeRecorder();
        var reporter = CreateReporter(
            delivery,
            outcomes,
            new AutoAdvancingClock(),
            () => throw new InvalidOperationException("cache unavailable"));

        reporter.QueueOnFirstReady();
        await outcomes.Settled.WaitAsync(TestTimeout);

        Assert.Contains("I couldn't check this channel yet", Assert.Single(delivery.Reports));
        Assert.Equal([false], outcomes.Values);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static BeanBotStartupReport CreateReporter(
        IOwnerAlertDelivery delivery,
        OutcomeRecorder outcomes,
        TimeProvider clock,
        Func<PunChannelStartupStatus>? checkChannel = null,
        ILogger<BeanBotStartupReport>? logger = null)
        => new(
            checkChannel ?? (() => PunChannelStartupStatus.NotFound),
            "2.18.3",
            null,
            null,
            outcomes.Record,
            delivery,
            clock,
            logger);

    private sealed class OutcomeRecorder
    {
        private readonly TaskCompletionSource _settled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<bool> Values { get; } = new();
        public Task Settled => _settled.Task;

        public void Record(bool missingChannelAlertHandled)
        {
            Values.Enqueue(missingChannelAlertHandled);
            _settled.TrySetResult();
        }
    }

    private sealed class ScriptedDelivery : IOwnerAlertDelivery
    {
        private readonly TaskCompletionSource _firstSendCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int FailuresBeforeSuccess { get; init; }
        public bool BlockFirstSend { get; init; }
        public int CallCount => Volatile.Read(ref _callCount);
        public ConcurrentQueue<string> Reports { get; } = new();
        public TaskCompletionSource FirstSendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            Reports.Enqueue(alert);
            if (call == 1 && BlockFirstSend)
            {
                FirstSendStarted.TrySetResult();
                return _firstSendCompletion.Task;
            }

            return call <= FailuresBeforeSuccess
                ? Task.FromException(new InvalidOperationException("delivery unavailable"))
                : Task.CompletedTask;
        }

        public void ReleaseFirstSend(Exception? failure = null)
        {
            if (failure is null)
            {
                _firstSendCompletion.SetResult();
            }
            else
            {
                _firstSendCompletion.SetException(failure);
            }
        }
    }

    /// <summary>
    /// Fires every timer immediately and moves the clock forward by its due time. Timers shorter
    /// than the retry cap are retry delays; the longer ones are the delivery-window wait.
    /// </summary>
    private sealed class AutoAdvancingClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        private readonly object _sync = new();
        private readonly List<TimeSpan> _retryDelays = [];
        private DateTimeOffset _utcNow = Start;

        public TimeSpan Elapsed
        {
            get
            {
                lock (_sync)
                {
                    return _utcNow - Start;
                }
            }
        }

        public TimeSpan[] RetryDelays
        {
            get
            {
                lock (_sync)
                {
                    return [.. _retryDelays];
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                if (dueTime <= BeanBotStartupReport.MaximumRetryDelay)
                {
                    _retryDelays.Add(dueTime);
                }

                _utcNow = _utcNow.Add(dueTime);
            }

            return base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class NeverFiringClock : TimeProvider
    {
        public TaskCompletionSource TimerCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime <= BeanBotStartupReport.MaximumRetryDelay)
            {
                TimerCreated.TrySetResult();
            }

            return base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class RecordingLogger : ILogger<BeanBotStartupReport>
    {
        public ConcurrentQueue<LogLevel> Levels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Levels.Enqueue(logLevel);
    }
}
