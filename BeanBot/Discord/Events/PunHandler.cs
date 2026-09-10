using System.Globalization;
using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Logging;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

public sealed partial class PunHandler : IAsyncDisposable
{
    private readonly ulong _generalChannelId;
    private readonly IPunProvider _punProvider;
    private readonly IDailyPunClaimStore _claimStore;
    private readonly Func<Func<string, RequestOptions, Task>?> _resolveSendMessage;
    private readonly TimeProvider _timeProvider;
    private readonly DailyPunSchedule _schedule;
    private Task<DailyPunClaimResult>? _pendingClaim;
    private readonly PunSchedulerOptions _schedulerOptions;
    private readonly ILogger<PunHandler> _logger;
    private readonly CancellationTokenSource _tokenSource = new();
    private Task? _runner;
    private int _disposed;

    internal static readonly TimeSpan MessageSendTimeout = TimeSpan.FromSeconds(10);

    public PunHandler(
        DiscordSocketClient discordSocketClient,
        BeanBotOptions options,
        IPunProvider punProvider,
        IDailyPunClaimStore claimStore,
        ILogger<PunHandler> logger)
        : this(discordSocketClient, options, punProvider, claimStore, logger, TimeProvider.System)
    {
    }

    internal PunHandler(
        DiscordSocketClient discordSocketClient,
        BeanBotOptions options,
        IPunProvider punProvider,
        IDailyPunClaimStore claimStore,
        ILogger<PunHandler> logger,
        TimeProvider timeProvider)
        : this(
            options.GeneralChannelId,
            punProvider,
            claimStore,
            () => ResolveSender(discordSocketClient, options.GeneralChannelId),
            timeProvider,
            PunSchedulerOptions.Default,
            logger,
            options.DailyPun)
    {
        ArgumentNullException.ThrowIfNull(discordSocketClient);
    }

    internal PunHandler(
        ulong generalChannelId,
        IPunProvider punProvider,
        IDailyPunClaimStore claimStore,
        Func<Func<string, RequestOptions, Task>?> resolveSendMessage,
        TimeProvider timeProvider,
        PunSchedulerOptions schedulerOptions,
        ILogger<PunHandler> logger,
        DailyPunSchedule schedule)
    {
        _generalChannelId = generalChannelId;
        _punProvider = punProvider ?? throw new ArgumentNullException(nameof(punProvider));
        _claimStore = claimStore ?? throw new ArgumentNullException(nameof(claimStore));
        _resolveSendMessage = resolveSendMessage ?? throw new ArgumentNullException(nameof(resolveSendMessage));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _schedulerOptions = schedulerOptions ?? throw new ArgumentNullException(nameof(schedulerOptions));
        ValidateSchedulerOptions(_schedulerOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        BeanBotLog.PunServiceInitializing(_logger);
    }

    private static Func<string, RequestOptions, Task>? ResolveSender(
        DiscordSocketClient client, ulong channelId)
    {
        if (client.GetChannel(channelId) is not SocketTextChannel channel)
        {
            return null;
        }

        return async (message, options) => await channel.SendMessageAsync(message, options: options);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_runner is not null)
        {
            throw new InvalidOperationException("The daily pun service has already been started.");
        }

        _runner = Task.Run(() => RunAsync(_tokenSource.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        var timezone = _schedule.TimeZone;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var nowUtc = _timeProvider.GetUtcNow();
                var window = ComputeScheduleWindow(
                    timezone,
                    nowUtc,
                    _schedule.LocalTime,
                    _schedulerOptions.CatchUpGraceWindow);
                var localNow = TimeZoneInfo.ConvertTime(nowUtc, timezone);
                var today = DateOnly.FromDateTime(localNow.DateTime);
                if (window.LocalDate > today)
                {
                    LogGraceWindowExpired(today);
                }

                await RunOccurrenceAsync(window, timezone, token);
                await DelayUntilNextOccurrenceAsync(
                    timezone,
                    window.LocalDate.AddDays(1),
                    token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                BeanBotLog.PunServiceShuttingDown(_logger);
                break;
            }
            catch (Exception exception)
            {
                BeanBotLog.PunLoopFailed(_logger, exception);
                try
                {
                    await Task.Delay(_schedulerOptions.UnexpectedFailureRetryDelay, _timeProvider, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    BeanBotLog.PunServiceShuttingDown(_logger);
                    break;
                }
            }
        }
    }

    internal async Task<PunOccurrenceResult> RunOccurrenceAsync(
        PunScheduleWindow window,
        TimeZoneInfo timezone,
        CancellationToken token)
    {
        LogSchedule(timezone, window.ScheduledUtc);

        var delayUntilScheduled = window.ScheduledUtc - _timeProvider.GetUtcNow();
        if (delayUntilScheduled > TimeSpan.Zero)
        {
            await Task.Delay(delayUntilScheduled, _timeProvider, token);
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            var nowUtc = _timeProvider.GetUtcNow();
            if (nowUtc > window.GraceEndsUtc)
            {
                LogGraceWindowExpired(window.LocalDate);
                return PunOccurrenceResult.GraceExpired;
            }

            var localDate = window.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var isCatchUp = nowUtc > window.ScheduledUtc;
            LogPunAttempting(_logger, localDate, isCatchUp);

            var sendMessage = _resolveSendMessage();
            if (sendMessage is null)
            {
                BeanBotLog.PunChannelMissing(_logger, _generalChannelId);
                if (!await DelayForPreSendRetryAsync(window, localDate, "channel-unavailable", token))
                {
                    return PunOccurrenceResult.GraceExpired;
                }

                continue;
            }

            if (!_punProvider.TryGetRandomPun(out var pun))
            {
                if (!await DelayForPreSendRetryAsync(window, localDate, "pun-unavailable", token))
                {
                    return PunOccurrenceResult.GraceExpired;
                }

                continue;
            }

            DailyPunClaimResult claimResult;
            try
            {
                claimResult = await TryClaimWithTimeoutAsync(window.LocalDate, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogPunClaimStoreFailed(_logger, localDate, exception);
                if (!await DelayForPreSendRetryAsync(window, localDate, "claim-store-unavailable", token))
                {
                    return PunOccurrenceResult.GraceExpired;
                }

                continue;
            }

            if (claimResult == DailyPunClaimResult.AlreadyClaimed)
            {
                LogPunDuplicateSuppressed(_logger, localDate);
                return PunOccurrenceResult.DuplicateSuppressed;
            }

            token.ThrowIfCancellationRequested();
            if (_timeProvider.GetUtcNow() > window.GraceEndsUtc)
            {
                LogPunGraceWindowExpired(_logger, localDate);
                return PunOccurrenceResult.GraceExpiredAfterClaim;
            }

            var localNow = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timezone);
            PunScheduleLog.Posting(_logger, localNow, _schedule.TimeZoneId);
            var requestOptions = new RequestOptions { CancelToken = token };
            try
            {
                await SendPunMessagesAsync(
                    sendMessage,
                    pun,
                    requestOptions,
                    _logger,
                    _schedulerOptions.MessageSendTimeout);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                BeanBotLog.PunPostingFailed(_logger, exception);
            }

            return PunOccurrenceResult.Attempted;
        }
    }

    private async Task<DailyPunClaimResult> TryClaimWithTimeoutAsync(
        DateOnly localDate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // A timed-out Mongo operation still owns the single claim slot until it actually settles.
        // Its result is deliberately discarded: a fresh durable claim must authorize every send.
        if (_pendingClaim is { IsCompleted: false })
        {
            throw new TimeoutException("The previous daily pun claim has not completed.");
        }

        _pendingClaim = ClaimAsync(localDate, token);
        ObserveLateFault(_pendingClaim);
        return await _pendingClaim.WaitAsync(_schedulerOptions.ClaimAttemptTimeout, token);
    }

    private async Task<DailyPunClaimResult> ClaimAsync(DateOnly localDate, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(_schedulerOptions.ClaimAttemptTimeout);
        return await _claimStore.TryClaimAsync(localDate, cancellation.Token);
    }

    private async Task<bool> DelayForPreSendRetryAsync(
        PunScheduleWindow window,
        string localDate,
        string reason,
        CancellationToken token)
    {
        var remaining = window.GraceEndsUtc - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            LogPunGraceWindowExpired(_logger, localDate);
            return false;
        }

        var delay = remaining < _schedulerOptions.PreflightRetryDelay
            ? remaining
            : _schedulerOptions.PreflightRetryDelay;
        LogPunPreflightRetrying(_logger, localDate, reason, delay);
        await Task.Delay(delay, _timeProvider, token);
        return _timeProvider.GetUtcNow() <= window.GraceEndsUtc;
    }

    private async Task DelayUntilNextOccurrenceAsync(
        TimeZoneInfo timezone,
        DateOnly nextDate,
        CancellationToken token)
    {
        var nextRunUtc = ComputeOccurrenceUtc(timezone, nextDate, _schedule.LocalTime);
        LogSchedule(timezone, nextRunUtc);
        var delay = nextRunUtc - _timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, token);
        }
    }

    private void LogSchedule(TimeZoneInfo timezone, DateTimeOffset nextRunUtc)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var nextLocal = TimeZoneInfo.ConvertTime(nextRunUtc, timezone)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var nextUtc = nextRunUtc.UtcDateTime
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var nowLocal = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timezone)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        PunScheduleLog.Scheduled(
            _logger,
            nextLocal,
            _schedule.TimeZoneId,
            nextUtc,
            nowLocal);
    }

    private void LogGraceWindowExpired(DateOnly localDate)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var formattedDate = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        LogPunGraceWindowExpired(_logger, formattedDate);
    }

    internal static async Task SendPunMessagesAsync(
        Func<string, RequestOptions, Task> sendMessage,
        string pun,
        RequestOptions requestOptions,
        ILogger logger,
        TimeSpan? sendTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(sendMessage);
        ArgumentNullException.ThrowIfNull(requestOptions);
        ArgumentNullException.ThrowIfNull(logger);

        var timeout = sendTimeout ?? MessageSendTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sendTimeout), "Send timeout must be greater than zero.");
        }

        var token = requestOptions.CancelToken;
        await SendWithTimeoutAsync(
            sendMessage,
            "The time has come and so have I, Bean Bot here to deliver you your daily pun(?)",
            requestOptions,
            timeout,
            token);
        await SendWithTimeoutAsync(
            sendMessage,
            "<:420stolfoit:675553715759087618>",
            requestOptions,
            timeout,
            token);
        try
        {
            await SendWithTimeoutAsync(
                sendMessage,
                pun,
                requestOptions,
                timeout,
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.PunPostingFailed(logger, exception);
        }
    }

    private static async Task SendWithTimeoutAsync(
        Func<string, RequestOptions, Task> sendMessage,
        string message,
        RequestOptions requestOptions,
        TimeSpan timeout,
        CancellationToken token)
    {
        var sendTask = sendMessage(message, requestOptions);
        try
        {
            await sendTask.WaitAsync(timeout, token);
        }
        catch (TimeoutException)
        {
            ObserveLateFault(sendTask);
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ObserveLateFault(sendTask);
            throw;
        }
    }

    private static void ObserveLateFault(Task sendTask)
    {
        if (sendTask.IsCompleted)
        {
            _ = sendTask.Exception;
            return;
        }

        _ = sendTask.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static DateTimeOffset ComputeNextOccurrenceUtc(
        DailyPunSchedule schedule,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var currentLocalTime = TimeZoneInfo.ConvertTime(nowUtc, schedule.TimeZone);
        var tentativeNextPostTime = new DateTime(
            currentLocalTime.Year,
            currentLocalTime.Month,
            currentLocalTime.Day,
            schedule.LocalTime.Hours,
            schedule.LocalTime.Minutes,
            schedule.LocalTime.Seconds,
            DateTimeKind.Unspecified);

        var nextOccurrenceUtc = ResolveOccurrenceUtc(schedule.TimeZone, tentativeNextPostTime);
        if (nextOccurrenceUtc <= nowUtc)
        {
            nextOccurrenceUtc = ResolveOccurrenceUtc(
                schedule.TimeZone,
                tentativeNextPostTime.AddDays(1));
        }

        return nextOccurrenceUtc;
    }

    private static DateTimeOffset ResolveOccurrenceUtc(
        TimeZoneInfo timeZone,
        DateTime localOccurrence)
    {
        if (timeZone.IsInvalidTime(localOccurrence))
        {
            localOccurrence = localOccurrence.AddHours(1);
        }
        else if (timeZone.IsAmbiguousTime(localOccurrence))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(localOccurrence);
            var preferredOffset = offsets.Contains(timeZone.BaseUtcOffset)
                ? timeZone.BaseUtcOffset
                : offsets.Min();
            return new DateTimeOffset(localOccurrence, preferredOffset).ToUniversalTime();
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(localOccurrence, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    internal static PunScheduleWindow ComputeScheduleWindow(
        TimeZoneInfo timezone,
        DateTimeOffset nowUtc,
        TimeSpan localTime,
        TimeSpan catchUpGraceWindow)
    {
        ArgumentNullException.ThrowIfNull(timezone);
        if (catchUpGraceWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(catchUpGraceWindow),
                "Catch-up grace window must be greater than zero.");
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timezone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var previous = CreateScheduleWindow(timezone, localDate.AddDays(-1), localTime, catchUpGraceWindow);
        if (nowUtc >= previous.ScheduledUtc && nowUtc <= previous.GraceEndsUtc)
        {
            return previous;
        }

        var today = CreateScheduleWindow(timezone, localDate, localTime, catchUpGraceWindow);
        if (nowUtc <= today.GraceEndsUtc)
        {
            return today;
        }

        return CreateScheduleWindow(
            timezone,
            localDate.AddDays(1),
            localTime,
            catchUpGraceWindow);
    }

    internal static PunScheduleWindow CreateScheduleWindow(
        TimeZoneInfo timezone,
        DateOnly localDate,
        TimeSpan localTime,
        TimeSpan catchUpGraceWindow)
    {
        var scheduledUtc = ComputeOccurrenceUtc(timezone, localDate, localTime);
        return new PunScheduleWindow(
            localDate,
            scheduledUtc,
            scheduledUtc.Add(catchUpGraceWindow));
    }

    internal static DateTimeOffset ComputeOccurrenceUtc(
        TimeZoneInfo timezone,
        DateOnly localDate,
        TimeSpan localTime)
    {
        ArgumentNullException.ThrowIfNull(timezone);
        var tentativePostTime = localDate.ToDateTime(
            TimeOnly.FromTimeSpan(localTime),
            DateTimeKind.Unspecified);

        return ResolveOccurrenceUtc(timezone, tentativePostTime);
    }

    private static void ValidateSchedulerOptions(PunSchedulerOptions options)
    {
        if (options.CatchUpGraceWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Catch-up grace window must be greater than zero.");
        }

        if (options.PreflightRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Preflight retry delay must be greater than zero.");
        }

        if (options.ClaimAttemptTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Claim attempt timeout must be greater than zero.");
        }

        if (options.UnexpectedFailureRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Unexpected failure retry delay must be greater than zero.");
        }

        if (options.MessageSendTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Message send timeout must be greater than zero.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _tokenSource.Cancel();
            if (_runner is not null)
            {
                await _runner.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _tokenSource.Dispose();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Attempting daily pun for local date {LocalDate}. CatchUp={CatchUp}")]
    private static partial void LogPunAttempting(ILogger logger, string localDate, bool catchUp);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Daily pun for local date {LocalDate} was already claimed; suppressing duplicate delivery")]
    private static partial void LogPunDuplicateSuppressed(ILogger logger, string localDate);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Daily pun catch-up grace window expired for local date {LocalDate}; advancing to the next day")]
    private static partial void LogPunGraceWindowExpired(ILogger logger, string localDate);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Daily pun claim store failed for local date {LocalDate}; failing closed before Discord send")]
    private static partial void LogPunClaimStoreFailed(
        ILogger logger,
        string localDate,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Daily pun pre-send retry scheduled for local date {LocalDate}. Reason={Reason}, Delay={Delay}")]
    private static partial void LogPunPreflightRetrying(
        ILogger logger,
        string localDate,
        string reason,
        TimeSpan delay);
}

internal readonly record struct PunScheduleWindow(
    DateOnly LocalDate,
    DateTimeOffset ScheduledUtc,
    DateTimeOffset GraceEndsUtc);

internal enum PunOccurrenceResult
{
    Attempted,
    DuplicateSuppressed,
    GraceExpired,
    GraceExpiredAfterClaim
}

internal sealed record PunSchedulerOptions(
    TimeSpan CatchUpGraceWindow,
    TimeSpan PreflightRetryDelay,
    TimeSpan ClaimAttemptTimeout,
    TimeSpan UnexpectedFailureRetryDelay,
    TimeSpan MessageSendTimeout)
{
    internal static PunSchedulerOptions Default { get; } = new(
        TimeSpan.FromMinutes(45),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        PunHandler.MessageSendTimeout);
}
