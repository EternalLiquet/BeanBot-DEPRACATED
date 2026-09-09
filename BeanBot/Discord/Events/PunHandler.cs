using System.Globalization;
using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Logging;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

public sealed class PunHandler : IAsyncDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly ulong _generalChannelId;
    private readonly DailyPunSchedule _schedule;
    private readonly IPunProvider _punProvider;
    private readonly ILogger<PunHandler> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _tokenSource = new();
    private Task? _runner;
    private int _disposed;

    internal static readonly TimeSpan MessageSendTimeout = TimeSpan.FromSeconds(10);

    public PunHandler(
        DiscordSocketClient discordSocketClient,
        BeanBotOptions options,
        IPunProvider punProvider,
        ILogger<PunHandler> logger)
        : this(
            discordSocketClient,
            options,
            punProvider,
            logger,
            TimeProvider.System)
    {
    }

    internal PunHandler(
        DiscordSocketClient discordSocketClient,
        BeanBotOptions options,
        IPunProvider punProvider,
        ILogger<PunHandler> logger,
        TimeProvider timeProvider)
    {
        _discordClient = discordSocketClient ?? throw new ArgumentNullException(nameof(discordSocketClient));
        var configuredOptions = options ?? throw new ArgumentNullException(nameof(options));
        _generalChannelId = configuredOptions.GeneralChannelId;
        _schedule = configuredOptions.DailyPun;
        _punProvider = punProvider ?? throw new ArgumentNullException(nameof(punProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
        while (!token.IsCancellationRequested)
        {
            try
            {
                var nowUtc = _timeProvider.GetUtcNow();
                var nextRunUtc = ComputeNextOccurrenceUtc(_schedule, nowUtc);
                var delay = nextRunUtc - nowUtc;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    var nextLocal = TimeZoneInfo.ConvertTime(nextRunUtc, _schedule.TimeZone)
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var nextUtc = nextRunUtc.UtcDateTime
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, _schedule.TimeZone)
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    PunScheduleLog.Scheduled(
                        _logger,
                        nextLocal,
                        nextUtc,
                        nowLocal,
                        _schedule.TimeZoneId);
                }

                await Task.Delay(delay, _timeProvider, token);

                await PostDailyAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                BeanBotLog.PunServiceShuttingDown(_logger);
            }
            catch (Exception ex)
            {
                BeanBotLog.PunLoopFailed(_logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(30), _timeProvider, token);
            }
        }
    }

    private async Task PostDailyAsync(CancellationToken token)
    {
        var localNow = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _schedule.TimeZone);
        PunScheduleLog.Posting(_logger, localNow, _schedule.TimeZoneId);

        var channel = _discordClient.GetChannel(_generalChannelId) as SocketTextChannel;
        if (channel is null)
        {
            BeanBotLog.PunChannelMissing(_logger, _generalChannelId);
            return;
        }

        if (!_punProvider.TryGetRandomPun(out var pun))
        {
            return;
        }

        var requestOptions = new RequestOptions { CancelToken = token };
        await SendPunMessagesAsync(
            async (message, options) =>
                await channel.SendMessageAsync(message, options: options),
            pun,
            requestOptions,
            _logger);
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

        if (currentLocalTime.TimeOfDay >= schedule.LocalTime)
        {
            tentativeNextPostTime = tentativeNextPostTime.AddDays(1);
        }

        if (schedule.TimeZone.IsInvalidTime(tentativeNextPostTime))
        {
            tentativeNextPostTime = tentativeNextPostTime.AddHours(1);
        }
        else if (schedule.TimeZone.IsAmbiguousTime(tentativeNextPostTime))
        {
            var offsets = schedule.TimeZone.GetAmbiguousTimeOffsets(tentativeNextPostTime);
            var preferredOffset = offsets.Contains(schedule.TimeZone.BaseUtcOffset)
                ? schedule.TimeZone.BaseUtcOffset
                : offsets.Min();
            return new DateTimeOffset(tentativeNextPostTime, preferredOffset).ToUniversalTime();
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(tentativeNextPostTime, schedule.TimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
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
        catch (OperationCanceledException) { }
        _tokenSource.Dispose();
    }
}
