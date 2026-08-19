using System.Globalization;
using BeanBot.Configuration;
using BeanBot.Services;
using BeanBot.Util;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.EventHandlers;

public sealed class PunHandler : IAsyncDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly ulong _generalChannelId;
    private readonly IPunProvider _punProvider;
    private readonly ILogger<PunHandler> _logger;
    private readonly CancellationTokenSource _tokenSource = new();
    private Task? _runner;
    private int _disposed;

    private static readonly TimeSpan PostTimeLocal = new(16, 20, 0);

    public PunHandler(
        DiscordSocketClient discordSocketClient,
        BeanBotOptions options,
        IPunProvider punProvider,
        ILogger<PunHandler> logger)
    {
        _discordClient = discordSocketClient ?? throw new ArgumentNullException(nameof(discordSocketClient));
        _generalChannelId = (options ?? throw new ArgumentNullException(nameof(options))).GeneralChannelId;
        _punProvider = punProvider ?? throw new ArgumentNullException(nameof(punProvider));
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
                var nextRunUtc = ComputeNextOccurenceUtc(timezone, PostTimeLocal);
                var delay = nextRunUtc - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                var chicagoNow = GetChicagoNow(timezone);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    var nextLocal = TimeZoneInfo.ConvertTime(nextRunUtc, timezone)
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var nextUtc = nextRunUtc.UtcDateTime
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var nowLocal = chicagoNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    BeanBotLog.PunScheduled(
                        _logger,
                        nextLocal,
                        nextUtc,
                        nowLocal);
                }

                await Task.Delay(delay, token);

                await PostDailyAsync(timezone, token);
            }
            catch (OperationCanceledException)
            {
                BeanBotLog.PunServiceShuttingDown(_logger);
            }
            catch (Exception ex)
            {
                BeanBotLog.PunLoopFailed(_logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
    }

    private async Task PostDailyAsync(TimeZoneInfo timezone, CancellationToken token)
    {
        var chicagoNow = GetChicagoNow(timezone);
        BeanBotLog.PunPosting(_logger, chicagoNow);

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

        await channel.SendMessageAsync("The time has come and so have I, Bean Bot here to deliver you your daily pun(?)");
        await channel.SendMessageAsync("<:420stolfoit:675553715759087618>");
        try
        {
            await channel.SendMessageAsync(pun);
        }
        catch (Exception exception)
        {
            BeanBotLog.PunPostingFailed(_logger, exception);
        }
    }

    private static TimeZoneInfo GetChicagoTimeZone()
    {
        try
        {
            // If on Linux or MacOS
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            // If on Windows
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
    }

    private static DateTimeOffset GetChicagoNow(TimeZoneInfo timezone)
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timezone);


    private static DateTimeOffset ComputeNextOccurenceUtc(TimeZoneInfo timezone, TimeSpan localTime)
    {
        var currentChicagoTime = GetChicagoNow(timezone);
        var tentativeNextPostTime = new DateTime(
            currentChicagoTime.Year,
            currentChicagoTime.Month,
            currentChicagoTime.Day,
            localTime.Hours,
            localTime.Minutes,
            localTime.Seconds,
            DateTimeKind.Unspecified
        );

        if (currentChicagoTime.TimeOfDay >= localTime)
        {
            tentativeNextPostTime = tentativeNextPostTime.AddDays(1);
        }

        if (timezone.IsInvalidTime(tentativeNextPostTime))
        {
            tentativeNextPostTime = tentativeNextPostTime.AddHours(1);
        }
        else if (timezone.IsAmbiguousTime(tentativeNextPostTime))
        {
            return new DateTimeOffset(tentativeNextPostTime, timezone.GetAmbiguousTimeOffsets(tentativeNextPostTime)[0]);
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(tentativeNextPostTime, timezone);

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
