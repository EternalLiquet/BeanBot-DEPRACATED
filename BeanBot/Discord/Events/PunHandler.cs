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
    private readonly IPunClock _clock;
    private readonly PunSchedulerOptions _schedulerOptions;
    private readonly ILogger<PunHandler> _logger;
    private readonly CancellationTokenSource _tokenSource = new();
    private Task? _runner;
    private int _disposed;

    private static readonly TimeSpan PostTimeLocal = new(16, 20, 0);
    internal static readonly TimeSpan MessageSendTimeout = TimeSpan.FromSeconds(10);

    public PunHandler(
        DiscordSocketClient discordSocketClient,
        BeanBotOptions options,
        IPunProvider punProvider,
        IDailyPunClaimStore claimStore,
        ILogger<PunHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(discordSocketClient);
        ArgumentNullException.ThrowIfNull(options);
        _generalChannelId = options.GeneralChannelId;
        _punProvider = punProvider ?? throw new ArgumentNullException(nameof(punProvider));
        _claimStore = claimStore ?? throw new ArgumentNullException(nameof(claimStore));
        _resolveSendMessage = () =>
        {
            var channel = discordSocketClient.GetChannel(_generalChannelId) as SocketTextChannel;
            if (channel is null)
            {
                return null;
            }

            return async (message, requestOptions) =>
                await channel.SendMessageAsync(message, options: requestOptions);
        };
        _clock = SystemPunClock.Instance;
        _schedulerOptions = PunSchedulerOptions.Default;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        BeanBotLog.PunServiceInitializing(_logger);
    }

    internal PunHandler(
        ulong generalChannelId,
        IPunProvider punProvider,
        IDailyPunClaimStore claimStore,
        Func<Func<string, RequestOptions, Task>?> resolveSendMessage,
        IPunClock clock,
        PunSchedulerOptions schedulerOptions,
        ILogger<PunHandler> logger)
    {
        _generalChannelId = generalChannelId;
        _punProvider = punProvider ?? throw new ArgumentNullException(nameof(punProvider));
        _claimStore = claimStore ?? throw new ArgumentNullException(nameof(claimStore));
        _resolveSendMessage = resolveSendMessage ?? throw new ArgumentNullException(nameof(resolveSendMessage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _schedulerOptions = schedulerOptions ?? throw new ArgumentNullException(nameof(schedulerOptions));
        ValidateSchedulerOptions(_schedulerOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        BeanBotLog.PunServiceInitializing(_logger);
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
        var timezone = GetChicagoTimeZone();

        while (!token.IsCancellationRequested)
        {
            try
            {
                var nowUtc = _clock.UtcNow;
                var window = ComputeScheduleWindow(
                    timezone,
                    nowUtc,
                    PostTimeLocal,
                    _schedulerOptions.CatchUpGraceWindow);
                var chicagoNow = TimeZoneInfo.ConvertTime(nowUtc, timezone);
                var today = DateOnly.FromDateTime(chicagoNow.DateTime);
                if (window.LocalDate > today)
                {
                    LogPunGraceWindowExpired(
                        _logger,
                        today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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
                    await _clock.DelayAsync(_schedulerOptions.UnexpectedFailureRetryDelay, token);
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

        var delayUntilScheduled = window.ScheduledUtc - _clock.UtcNow;
        if (delayUntilScheduled > TimeSpan.Zero)
        {
            await _clock.DelayAsync(delayUntilScheduled, token);
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            var nowUtc = _clock.UtcNow;
            if (nowUtc > window.GraceEndsUtc)
            {
                LogPunGraceWindowExpired(
                    _logger,
                    window.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return PunOccurrenceResult.GraceExpired;
            }

            var chicagoDate = window.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var isCatchUp = nowUtc > window.ScheduledUtc;
            LogPunAttempting(_logger, chicagoDate, isCatchUp);

            var sendMessage = _resolveSendMessage();
            if (sendMessage is null)
            {
                BeanBotLog.PunChannelMissing(_logger, _generalChannelId);
                if (!await DelayForPreSendRetryAsync(window, chicagoDate, "channel-unavailable", token))
                {
                    return PunOccurrenceResult.GraceExpired;
                }

                continue;
            }

            if (!_punProvider.TryGetRandomPun(out var pun))
            {
                if (!await DelayForPreSendRetryAsync(window, chicagoDate, "pun-unavailable", token))
                {
                    return PunOccurrenceResult.GraceExpired;
                }

                continue;
            }

            DailyPunClaimResult claimResult;
            try
            {
                using var claimCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                claimCancellation.CancelAfter(_schedulerOptions.ClaimAttemptTimeout);
                claimResult = await _claimStore.TryClaimAsync(
                    window.LocalDate,
                    claimCancellation.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogPunClaimStoreFailed(_logger, chicagoDate, exception);
                if (!await DelayForPreSendRetryAsync(window, chicagoDate, "claim-store-unavailable", token))
                {
                    return PunOccurrenceResult.GraceExpired;
                }

                continue;
            }

            if (claimResult == DailyPunClaimResult.AlreadyClaimed)
            {
                LogPunDuplicateSuppressed(_logger, chicagoDate);
                return PunOccurrenceResult.DuplicateSuppressed;
            }

            if (_clock.UtcNow > window.GraceEndsUtc)
            {
                LogPunGraceWindowExpired(_logger, chicagoDate);
                return PunOccurrenceResult.GraceExpiredAfterClaim;
            }

            var chicagoNow = TimeZoneInfo.ConvertTime(_clock.UtcNow, timezone);
            BeanBotLog.PunPosting(_logger, chicagoNow);
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

    private async Task<bool> DelayForPreSendRetryAsync(
        PunScheduleWindow window,
        string chicagoDate,
        string reason,
        CancellationToken token)
    {
        var remaining = window.GraceEndsUtc - _clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            LogPunGraceWindowExpired(_logger, chicagoDate);
            return false;
        }

        var delay = remaining < _schedulerOptions.PreflightRetryDelay
            ? remaining
            : _schedulerOptions.PreflightRetryDelay;
        LogPunPreflightRetrying(_logger, chicagoDate, reason, delay);
        await _clock.DelayAsync(delay, token);
        return _clock.UtcNow <= window.GraceEndsUtc;
    }

    private async Task DelayUntilNextOccurrenceAsync(
        TimeZoneInfo timezone,
        DateOnly nextDate,
        CancellationToken token)
    {
        var nextRunUtc = ComputeOccurrenceUtc(timezone, nextDate, PostTimeLocal);
        LogSchedule(timezone, nextRunUtc);
        var delay = nextRunUtc - _clock.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await _clock.DelayAsync(delay, token);
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
        var nowLocal = TimeZoneInfo.ConvertTime(_clock.UtcNow, timezone)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        BeanBotLog.PunScheduled(
            _logger,
            nextLocal,
            nextUtc,
            nowLocal);
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

    internal static TimeZoneInfo GetChicagoTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
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

        var chicagoNow = TimeZoneInfo.ConvertTime(nowUtc, timezone);
        var localDate = DateOnly.FromDateTime(chicagoNow.DateTime);
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

        if (timezone.IsInvalidTime(tentativePostTime))
        {
            tentativePostTime = tentativePostTime.AddHours(1);
        }
        else if (timezone.IsAmbiguousTime(tentativePostTime))
        {
            return new DateTimeOffset(
                    tentativePostTime,
                    timezone.GetAmbiguousTimeOffsets(tentativePostTime)[0])
                .ToUniversalTime();
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(tentativePostTime, timezone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
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
        Message = "Attempting daily pun for Chicago date {ChicagoDate}. CatchUp={CatchUp}")]
    private static partial void LogPunAttempting(ILogger logger, string chicagoDate, bool catchUp);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Daily pun for Chicago date {ChicagoDate} was already claimed; suppressing duplicate delivery")]
    private static partial void LogPunDuplicateSuppressed(ILogger logger, string chicagoDate);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Daily pun catch-up grace window expired for Chicago date {ChicagoDate}; advancing to the next day")]
    private static partial void LogPunGraceWindowExpired(ILogger logger, string chicagoDate);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Daily pun claim store failed for Chicago date {ChicagoDate}; failing closed before Discord send")]
    private static partial void LogPunClaimStoreFailed(
        ILogger logger,
        string chicagoDate,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Daily pun pre-send retry scheduled for Chicago date {ChicagoDate}. Reason={Reason}, Delay={Delay}")]
    private static partial void LogPunPreflightRetrying(
        ILogger logger,
        string chicagoDate,
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

internal interface IPunClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemPunClock : IPunClock
{
    internal static SystemPunClock Instance { get; } = new();

    private SystemPunClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
