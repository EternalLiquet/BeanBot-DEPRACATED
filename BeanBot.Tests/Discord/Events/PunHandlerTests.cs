using System.Diagnostics.CodeAnalysis;
using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Events;

public class PunHandlerTests
{
    private static readonly TimeSpan ScheduledLocalTime = new(16, 20, 0);
    private static readonly TimeSpan DefaultGraceWindow = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndPreventsStart()
    {
        using var client = new DiscordSocketClient();
        var options = CreateBeanBotOptions();
        var handler = new PunHandler(
            client,
            options,
            new UnavailablePunProvider(),
            new InMemoryClaimStore(),
            NullLogger<PunHandler>.Instance);

        await handler.DisposeAsync();
        await handler.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => handler.Start());
    }

    [Fact]
    public void ComputeScheduleWindow_BeforeScheduledTimeTargetsToday()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var nowUtc = new DateTimeOffset(2026, 9, 7, 20, 0, 0, TimeSpan.Zero);

        var window = PunHandler.ComputeScheduleWindow(
            timezone,
            nowUtc,
            ScheduledLocalTime,
            DefaultGraceWindow);

        Assert.Equal(new DateOnly(2026, 9, 7), window.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 21, 20, 0, TimeSpan.Zero), window.ScheduledUtc);
        Assert.Equal(window.ScheduledUtc.Add(DefaultGraceWindow), window.GraceEndsUtc);
    }

    [Fact]
    public void ComputeScheduleWindow_InsideGraceWindowTargetsToday()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var nowUtc = new DateTimeOffset(2026, 9, 7, 21, 30, 0, TimeSpan.Zero);

        var window = PunHandler.ComputeScheduleWindow(
            timezone,
            nowUtc,
            ScheduledLocalTime,
            DefaultGraceWindow);

        Assert.Equal(new DateOnly(2026, 9, 7), window.LocalDate);
    }

    [Fact]
    public void ComputeScheduleWindow_AfterGraceWindowTargetsTomorrow()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var nowUtc = new DateTimeOffset(2026, 9, 7, 22, 6, 0, TimeSpan.Zero);

        var window = PunHandler.ComputeScheduleWindow(
            timezone,
            nowUtc,
            ScheduledLocalTime,
            DefaultGraceWindow);

        Assert.Equal(new DateOnly(2026, 9, 8), window.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 21, 20, 0, TimeSpan.Zero), window.ScheduledUtc);
    }

    [Theory]
    [InlineData("2026-03-08", 21)]
    [InlineData("2026-11-01", 22)]
    public void ComputeOccurrenceUtc_UsesChicagoDstOffset(string localDateText, int expectedUtcHour)
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var localDate = DateOnly.Parse(localDateText, System.Globalization.CultureInfo.InvariantCulture);

        var occurrence = PunHandler.ComputeOccurrenceUtc(timezone, localDate, ScheduledLocalTime);

        Assert.Equal(expectedUtcHour, occurrence.Hour);
        Assert.Equal(20, occurrence.Minute);
        Assert.Equal(TimeSpan.Zero, occurrence.Offset);
    }

    [Fact]
    public async Task RunOccurrenceAsync_BeforeScheduleWaitsThenSendsExactlyOneSequence()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var clock = new AdvancingPunClock(
            new DateTimeOffset(2026, 9, 7, 20, 0, 0, TimeSpan.Zero));
        var claimStore = new InMemoryClaimStore();
        var messages = new List<string>();
        await using var handler = CreateHandler(
            claimStore,
            clock,
            () => CreateRecordingSender(messages));
        var window = PunHandler.CreateScheduleWindow(
            timezone,
            new DateOnly(2026, 9, 7),
            ScheduledLocalTime,
            DefaultGraceWindow);

        var result = await handler.RunOccurrenceAsync(window, timezone, CancellationToken.None);

        Assert.Equal(PunOccurrenceResult.Attempted, result);
        Assert.Equal([TimeSpan.FromMinutes(80)], clock.Delays);
        Assert.Equal(3, messages.Count);
        Assert.Equal(new DateOnly(2026, 9, 7), claimStore.ClaimedDate);
    }

    [Fact]
    public async Task RunOccurrenceAsync_InsideGraceWindowPerformsCatchUpOnce()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var clock = new AdvancingPunClock(
            new DateTimeOffset(2026, 9, 7, 21, 25, 0, TimeSpan.Zero));
        var claimStore = new InMemoryClaimStore();
        var messages = new List<string>();
        await using var handler = CreateHandler(
            claimStore,
            clock,
            () => CreateRecordingSender(messages));
        var window = PunHandler.CreateScheduleWindow(
            timezone,
            new DateOnly(2026, 9, 7),
            ScheduledLocalTime,
            DefaultGraceWindow);

        var result = await handler.RunOccurrenceAsync(window, timezone, CancellationToken.None);

        Assert.Equal(PunOccurrenceResult.Attempted, result);
        Assert.Empty(clock.Delays);
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public async Task RunOccurrenceAsync_PersistedClaimSuppressesRestartDuplicate()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var clock = new AdvancingPunClock(
            new DateTimeOffset(2026, 9, 7, 21, 25, 0, TimeSpan.Zero));
        var claimStore = new InMemoryClaimStore();
        Assert.Equal(
            DailyPunClaimResult.Acquired,
            await claimStore.TryClaimAsync(new DateOnly(2026, 9, 7), CancellationToken.None));
        var sends = 0;
        await using var handler = CreateHandler(
            claimStore,
            clock,
            () => (_, _) =>
            {
                sends++;
                return Task.CompletedTask;
            });
        var window = PunHandler.CreateScheduleWindow(
            timezone,
            new DateOnly(2026, 9, 7),
            ScheduledLocalTime,
            DefaultGraceWindow);

        var result = await handler.RunOccurrenceAsync(window, timezone, CancellationToken.None);

        Assert.Equal(PunOccurrenceResult.DuplicateSuppressed, result);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task RunOccurrenceAsync_ChannelUnavailableRetriesBeforeClaimThenSends()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var clock = new AdvancingPunClock(
            new DateTimeOffset(2026, 9, 7, 21, 25, 0, TimeSpan.Zero));
        var claimStore = new InMemoryClaimStore();
        var messages = new List<string>();
        var resolverCalls = 0;
        await using var handler = CreateHandler(
            claimStore,
            clock,
            () =>
            {
                resolverCalls++;
                return resolverCalls == 1 ? null : CreateRecordingSender(messages);
            });
        var window = PunHandler.CreateScheduleWindow(
            timezone,
            new DateOnly(2026, 9, 7),
            ScheduledLocalTime,
            DefaultGraceWindow);

        var result = await handler.RunOccurrenceAsync(window, timezone, CancellationToken.None);

        Assert.Equal(PunOccurrenceResult.Attempted, result);
        Assert.Equal(2, resolverCalls);
        Assert.Equal([TimeSpan.FromSeconds(30)], clock.Delays);
        Assert.Equal(3, messages.Count);
        Assert.Equal(1, claimStore.CallCount);
    }

    [Fact]
    public async Task RunOccurrenceAsync_ClaimStoreFailureFailsClosedThenRetries()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var clock = new AdvancingPunClock(
            new DateTimeOffset(2026, 9, 7, 21, 25, 0, TimeSpan.Zero));
        var claimStore = new FlakyClaimStore(new InMemoryClaimStore(), failuresBeforeSuccess: 1);
        var messages = new List<string>();
        await using var handler = CreateHandler(
            claimStore,
            clock,
            () => CreateRecordingSender(messages));
        var window = PunHandler.CreateScheduleWindow(
            timezone,
            new DateOnly(2026, 9, 7),
            ScheduledLocalTime,
            DefaultGraceWindow);

        var result = await handler.RunOccurrenceAsync(window, timezone, CancellationToken.None);

        Assert.Equal(PunOccurrenceResult.Attempted, result);
        Assert.Equal(2, claimStore.CallCount);
        Assert.Equal([TimeSpan.FromSeconds(30)], clock.Delays);
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public async Task RunOccurrenceAsync_ClaimStoreUnavailableUntilGraceExpiresNeverSends()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var scheduledUtc = new DateTimeOffset(2026, 9, 7, 21, 20, 0, TimeSpan.Zero);
        var clock = new AdvancingPunClock(scheduledUtc);
        var claimStore = new AlwaysFailingClaimStore();
        var sends = 0;
        var schedulerOptions = CreateSchedulerOptions(
            catchUpGraceWindow: TimeSpan.FromSeconds(10),
            preflightRetryDelay: TimeSpan.FromSeconds(5));
        await using var handler = CreateHandler(
            claimStore,
            clock,
            () => (_, _) =>
            {
                sends++;
                return Task.CompletedTask;
            },
            schedulerOptions);
        var window = PunHandler.CreateScheduleWindow(
            timezone,
            new DateOnly(2026, 9, 7),
            ScheduledLocalTime,
            schedulerOptions.CatchUpGraceWindow);

        var result = await handler.RunOccurrenceAsync(window, timezone, CancellationToken.None);

        Assert.Equal(PunOccurrenceResult.GraceExpired, result);
        Assert.Equal(0, sends);
        Assert.True(claimStore.CallCount >= 2);
        Assert.Equal(window.GraceEndsUtc, clock.UtcNow);
    }

    [Fact]
    public async Task RunOccurrenceAsync_SendFailureAfterClaimDoesNotAllowSecondSequence()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var clock = new AdvancingPunClock(
            new DateTimeOffset(2026, 9, 7, 21, 25, 0, TimeSpan.Zero));
        var claimStore = new InMemoryClaimStore();
        var sendInvocations = 0;
        await using var firstHandler = CreateHandler(
            claimStore,
            clock,
            () => (_, _) =>
            {
                sendInvocations++;
                return Task.FromException(new InvalidOperationException("ambiguous send failure"));
            });
        var window = PunHandler.CreateScheduleWindow(
            timezone,
            new DateOnly(2026, 9, 7),
            ScheduledLocalTime,
            DefaultGraceWindow);

        var firstResult = await firstHandler.RunOccurrenceAsync(window, timezone, CancellationToken.None);
        Assert.Equal(PunOccurrenceResult.Attempted, firstResult);
        Assert.Equal(1, sendInvocations);

        await using var restartedHandler = CreateHandler(
            claimStore,
            clock,
            () => (_, _) =>
            {
                sendInvocations++;
                return Task.CompletedTask;
            });
        var restartResult = await restartedHandler.RunOccurrenceAsync(
            window,
            timezone,
            CancellationToken.None);

        Assert.Equal(PunOccurrenceResult.DuplicateSuppressed, restartResult);
        Assert.Equal(1, sendInvocations);
    }

    [Fact]
    public async Task RunOccurrenceAsync_CancellationInterruptsScheduledWait()
    {
        var timezone = PunHandler.GetChicagoTimeZone();
        var clock = new BlockingPunClock(
            new DateTimeOffset(2026, 9, 7, 20, 0, 0, TimeSpan.Zero));
        await using var handler = CreateHandler(
            new InMemoryClaimStore(),
            clock,
            () => (_, _) => Task.CompletedTask);
        var window = PunHandler.CreateScheduleWindow(
            timezone,
            new DateOnly(2026, 9, 7),
            ScheduledLocalTime,
            DefaultGraceWindow);
        using var cancellation = new CancellationTokenSource();

        var running = handler.RunOccurrenceAsync(window, timezone, cancellation.Token);
        await clock.DelayStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task SendPunMessagesAsync_Success_SendsMessagesInOrder()
    {
        var messages = new List<string>();

        await PunHandler.SendPunMessagesAsync(
            (message, _) =>
            {
                messages.Add(message);
                return Task.CompletedTask;
            },
            "test pun",
            new RequestOptions(),
            NullLogger.Instance,
            TimeSpan.FromSeconds(1));

        Assert.Equal(
            [
                "The time has come and so have I, Bean Bot here to deliver you your daily pun(?)",
                "<:420stolfoit:675553715759087618>",
                "test pun"
            ],
            messages);
    }

    [Fact]
    public async Task SendPunMessagesAsync_FirstSendTimeout_IsBoundedAndDoesNotRetry()
    {
        var sends = 0;
        var stalledSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() => PunHandler.SendPunMessagesAsync(
            (_, _) =>
            {
                sends++;
                return stalledSend.Task;
            },
            "test pun",
            new RequestOptions(),
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(25)));

        Assert.Equal(1, sends);

        stalledSend.SetException(new InvalidOperationException("late failure"));
        await Task.Yield();
    }

    [Fact]
    public async Task SendPunMessagesAsync_FinalSendTimeout_IsLoggedAndDoesNotRetry()
    {
        var sends = 0;
        var stalledSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingLogger();

        await PunHandler.SendPunMessagesAsync(
            (_, _) =>
            {
                sends++;
                return sends == 3
                    ? stalledSend.Task
                    : Task.CompletedTask;
            },
            "test pun",
            new RequestOptions(),
            logger,
            TimeSpan.FromMilliseconds(25));

        Assert.Equal(3, sends);
        Assert.Contains(LogLevel.Error, logger.Levels);

        stalledSend.SetException(new InvalidOperationException("late failure"));
        await Task.Yield();
    }

    [Fact]
    public async Task SendPunMessagesAsync_CancellationDuringStalledSend_PropagatesWithoutErrorLog()
    {
        using var cancellation = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var sends = 0;
        var thirdSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalledSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var posting = PunHandler.SendPunMessagesAsync(
            (_, _) =>
            {
                sends++;
                if (sends == 3)
                {
                    thirdSendStarted.SetResult();
                    return stalledSend.Task;
                }

                return Task.CompletedTask;
            },
            "test pun",
            new RequestOptions { CancelToken = cancellation.Token },
            logger,
            TimeSpan.FromSeconds(1));

        await thirdSendStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => posting);
        Assert.Equal(3, sends);
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Error);

        stalledSend.SetException(new InvalidOperationException("late failure"));
        await Task.Yield();
    }

    [Fact]
    public async Task ThirdMessageCancellationPropagatesWithoutErrorLog()
    {
        using var cancellation = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var sends = 0;

        var posting = PunHandler.SendPunMessagesAsync(
            (_, _) =>
            {
                sends++;
                if (sends == 3)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(cancellation.Token);
                }

                return Task.CompletedTask;
            },
            "test pun",
            new RequestOptions { CancelToken = cancellation.Token },
            logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => posting);
        Assert.Equal(3, sends);
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Error);
    }

    private static PunHandler CreateHandler(
        IDailyPunClaimStore claimStore,
        IPunClock clock,
        Func<Func<string, RequestOptions, Task>?> resolveSendMessage,
        PunSchedulerOptions? schedulerOptions = null)
        => new(
            123,
            new StaticPunProvider("test pun"),
            claimStore,
            resolveSendMessage,
            clock,
            schedulerOptions ?? CreateSchedulerOptions(),
            NullLogger<PunHandler>.Instance);

    private static PunSchedulerOptions CreateSchedulerOptions(
        TimeSpan? catchUpGraceWindow = null,
        TimeSpan? preflightRetryDelay = null)
        => new(
            catchUpGraceWindow ?? DefaultGraceWindow,
            preflightRetryDelay ?? TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1));

    private static Func<string, RequestOptions, Task> CreateRecordingSender(List<string> messages)
        => (message, _) =>
        {
            messages.Add(message);
            return Task.CompletedTask;
        };

    private static BeanBotOptions CreateBeanBotOptions()
        => new(
            "token",
            "mongodb://localhost",
            1,
            new Uri("https://example.com/hatoete"),
            new Uri("https://example.com/yoshimaru"),
            HealthCheckOptions.Disabled);

    private sealed class StaticPunProvider(string pun) : IPunProvider
    {
        public bool TryGetRandomPun([NotNullWhen(true)] out string? value)
        {
            value = pun;
            return true;
        }
    }

    private sealed class UnavailablePunProvider : IPunProvider
    {
        public bool TryGetRandomPun([NotNullWhen(true)] out string? pun)
        {
            pun = null;
            return false;
        }
    }

    private sealed class InMemoryClaimStore : IDailyPunClaimStore
    {
        private readonly object _gate = new();
        private DateOnly? _claimedDate;

        public int CallCount { get; private set; }
        public DateOnly? ClaimedDate => _claimedDate;

        public Task<DailyPunClaimResult> TryClaimAsync(
            DateOnly chicagoDate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                CallCount++;
                if (_claimedDate == chicagoDate)
                {
                    return Task.FromResult(DailyPunClaimResult.AlreadyClaimed);
                }

                _claimedDate = chicagoDate;
                return Task.FromResult(DailyPunClaimResult.Acquired);
            }
        }
    }

    private sealed class FlakyClaimStore(
        IDailyPunClaimStore inner,
        int failuresBeforeSuccess) : IDailyPunClaimStore
    {
        private int _remainingFailures = failuresBeforeSuccess;

        public int CallCount { get; private set; }

        public Task<DailyPunClaimResult> TryClaimAsync(
            DateOnly chicagoDate,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (_remainingFailures-- > 0)
            {
                throw new InvalidOperationException("claim store unavailable");
            }

            return inner.TryClaimAsync(chicagoDate, cancellationToken);
        }
    }

    private sealed class AlwaysFailingClaimStore : IDailyPunClaimStore
    {
        public int CallCount { get; private set; }

        public Task<DailyPunClaimResult> TryClaimAsync(
            DateOnly chicagoDate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromException<DailyPunClaimResult>(
                new InvalidOperationException($"claim unavailable for {chicagoDate}"));
        }
    }

    private sealed class AdvancingPunClock(DateTimeOffset utcNow) : IPunClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow = UtcNow.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingPunClock(DateTimeOffset utcNow) : IPunClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
        public TaskCompletionSource DelayStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
