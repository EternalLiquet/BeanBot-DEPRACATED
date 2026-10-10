using System.Text;
using BeanBot.Configuration;
using BeanBot.Discord.Puns;
using BeanBot.Logging;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeanBot.Hosting;

internal enum PunChannelStartupState
{
    Ready,
    NotFound,
    MissingPermission,
    PermissionCheckUnavailable
}

internal readonly record struct PunChannelStartupStatus(
    PunChannelStartupState State,
    string? Name,
    bool? CanView,
    bool? CanSend)
{
    internal static PunChannelStartupStatus NotFound { get; } =
        new(PunChannelStartupState.NotFound, null, null, null);

    internal static PunChannelStartupStatus PermissionCheckUnavailable { get; } =
        new(PunChannelStartupState.PermissionCheckUnavailable, null, null, null);

    internal static PunChannelStartupStatus FromCachedChannel(
        string name,
        bool? canView,
        bool? canSend)
    {
        if (canView is null || canSend is null)
        {
            return new(PunChannelStartupState.PermissionCheckUnavailable, name, canView, canSend);
        }

        return new(
            canView.Value && canSend.Value
                ? PunChannelStartupState.Ready
                : PunChannelStartupState.MissingPermission,
            name,
            canView,
            canSend);
    }
}

internal sealed class BeanBotStartupReport : IAsyncDisposable
{
    internal static readonly TimeSpan DeliveryWindow = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    private readonly Func<PunChannelStartupStatus> _checkChannel;
    private readonly string _version;
    private readonly Uri? _hatoeteUrl;
    private readonly Uri? _yoshimaruUrl;
    private readonly Action<bool> _recordDeliveryOutcome;
    private readonly IOwnerAlertDelivery _delivery;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BeanBotStartupReport> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleSync = new();
    private Task? _deliveryLoop;
    private Task? _activeSend;
    private bool _started;
    private bool _stopped;
    private int _disposed;

    public BeanBotStartupReport(
        DiscordSocketClient discordClient,
        BeanBotOptions options,
        DailyPunService dailyPunService,
        IOwnerAlertDelivery delivery,
        ILogger<BeanBotStartupReport> logger)
        : this(
            () => CheckChannel(discordClient, options.GeneralChannelId),
            BuildIdentity.Current.Version,
            options.HatoeteImageUrl,
            options.YoshimaruImageUrl,
            dailyPunService.RecordStartupReportOutcome,
            delivery,
            TimeProvider.System,
            logger)
    {
    }

    /// <param name="recordDeliveryOutcome">
    /// Called exactly once when the report settles. True means the owner received a report
    /// that already said the daily pun channel is unavailable, so the scheduled alert is handled.
    /// </param>
    internal BeanBotStartupReport(
        Func<PunChannelStartupStatus> checkChannel,
        string version,
        Uri? hatoeteUrl,
        Uri? yoshimaruUrl,
        Action<bool> recordDeliveryOutcome,
        IOwnerAlertDelivery delivery,
        TimeProvider timeProvider,
        ILogger<BeanBotStartupReport>? logger = null)
    {
        _checkChannel = checkChannel ?? throw new ArgumentNullException(nameof(checkChannel));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _hatoeteUrl = hatoeteUrl;
        _yoshimaruUrl = yoshimaruUrl;
        _recordDeliveryOutcome = recordDeliveryOutcome
            ?? throw new ArgumentNullException(nameof(recordDeliveryOutcome));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<BeanBotStartupReport>.Instance;
    }

    internal bool HasActiveDiscordOperation
    {
        get
        {
            lock (_lifecycleSync)
            {
                return _deliveryLoop is { IsCompleted: false }
                    || _activeSend is { IsCompleted: false };
            }
        }
    }

    internal void QueueOnFirstReady()
    {
        lock (_lifecycleSync)
        {
            if (_started || _stopped)
            {
                return;
            }

            _started = true;
        }

        PunChannelStartupStatus channel;
        try
        {
            channel = _checkChannel();
        }
        catch (Exception exception)
        {
            BeanBotLog.StartupChannelCheckFailed(_logger, exception);
            channel = PunChannelStartupStatus.PermissionCheckUnavailable;
        }

        var report = Format(_version, channel, _hatoeteUrl, _yoshimaruUrl);
        var reportsUnavailableChannel = channel.State is
            PunChannelStartupState.NotFound or PunChannelStartupState.MissingPermission;
        lock (_lifecycleSync)
        {
            if (!_stopped)
            {
                _deliveryLoop = Task.Run(
                    () => DeliverAndRecordOutcomeAsync(report, reportsUnavailableChannel, _shutdown.Token));
                return;
            }
        }

        _recordDeliveryOutcome(false);
    }

    internal async Task StopAsync()
    {
        bool firstStop;
        Task? deliveryLoop;
        lock (_lifecycleSync)
        {
            firstStop = !_stopped;
            _stopped = true;
            deliveryLoop = _deliveryLoop;
        }

        if (firstStop)
        {
            _shutdown.Cancel();
        }

        if (deliveryLoop is not null)
        {
            await deliveryLoop;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync();
        _shutdown.Dispose();
    }

    private async Task DeliverAndRecordOutcomeAsync(
        string report,
        bool reportsUnavailableChannel,
        CancellationToken cancellationToken)
    {
        var delivered = false;
        try
        {
            delivered = await DeliverWithinWindowAsync(report, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown stopped the retries; the outcome below still settles the pun alert.
        }
        catch (Exception exception)
        {
            BeanBotLog.StartupReportFailed(_logger, exception);
        }
        finally
        {
            _recordDeliveryOutcome(delivered && reportsUnavailableChannel);
        }
    }

    private async Task<bool> DeliverWithinWindowAsync(string report, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + DeliveryWindow;
        Exception? lastFailure = null;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // The next attempt starts only after this send settles, so a stalled
            // Discord call can never overlap with a retry of the same report.
            var send = StartSend(report, cancellationToken);
            try
            {
                await send.WaitAsync(remaining, _timeProvider, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException exception) when (!send.IsCompleted)
            {
                lastFailure = exception;
                break;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
            }

            var retryDelay = RetryDelay(attempt);
            remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= retryDelay)
            {
                break;
            }

            await Task.Delay(retryDelay, _timeProvider, cancellationToken);
        }

        BeanBotLog.StartupReportDeliveryExpired(_logger, DeliveryWindow, lastFailure);
        return false;
    }

    private Task StartSend(string report, CancellationToken cancellationToken)
    {
        Task send;
        try
        {
            send = _delivery.DeliverAsync(report, cancellationToken);
        }
        catch (Exception exception)
        {
            send = Task.FromException(exception);
        }

        lock (_lifecycleSync)
        {
            _activeSend = send;
        }

        // A send abandoned at the deadline or during shutdown may still fail later.
        _ = send.ContinueWith(
            completedSend =>
            {
                _ = completedSend.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return send;
    }

    internal static TimeSpan RetryDelay(int attempt)
    {
        var seconds = Math.Pow(2, Math.Min(attempt - 1, 5));
        var delay = TimeSpan.FromSeconds(seconds);
        return delay < MaximumRetryDelay ? delay : MaximumRetryDelay;
    }

    internal static string Format(
        string version,
        PunChannelStartupStatus channel,
        Uri? hatoeteUrl,
        Uri? yoshimaruUrl)
    {
        var heading = version.Trim() switch
        {
            "0.0.0-local" => "Bean Bot development build started.",
            "" or "unknown" => "Bean Bot started (version unavailable).",
            var releaseVersion => $"Bean Bot v{releaseVersion} started."
        };

        var channelName = SafeChannelName(channel.Name);
        var channelLine = channel.State switch
        {
            PunChannelStartupState.Ready =>
                $"Daily pun channel: {channelName} — Ready (View Channel and Send Messages available).",
            PunChannelStartupState.NotFound =>
                "Daily pun channel: not found. Check the channel setting and my access.",
            PunChannelStartupState.MissingPermission =>
                $"Daily pun channel: {channelName} — missing permissions: {MissingPermissions(channel)}. " +
                "Check my access.",
            _ => $"Daily pun channel: {channelName} — I couldn't check this channel yet."
        };

        return string.Join('\n',
            heading,
            channelLine,
            $"Hatoete image URL: {UrlStatus(hatoeteUrl)}",
            $"Yoshimaru image URL: {UrlStatus(yoshimaruUrl)}");
    }

    private static PunChannelStartupStatus CheckChannel(DiscordSocketClient client, ulong channelId)
    {
        if (client.GetChannel(channelId) is not SocketTextChannel channel)
        {
            return PunChannelStartupStatus.NotFound;
        }

        var bot = channel.Guild.CurrentUser;
        if (bot is null)
        {
            return PunChannelStartupStatus.FromCachedChannel(channel.Name, null, null);
        }

        var permissions = bot.GetPermissions(channel);
        return PunChannelStartupStatus.FromCachedChannel(
            channel.Name,
            permissions.ViewChannel,
            permissions.SendMessages);
    }

    private static string SafeChannelName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "the chosen channel";
        }

        var safe = new StringBuilder(Math.Min(name.Length, 64));
        foreach (var character in name.AsSpan(0, Math.Min(name.Length, 64)))
        {
            safe.Append(char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-');
        }

        var safeName = safe.ToString().Trim('-');
        return safeName.Length == 0 ? "the chosen channel" : $"#{safeName}";
    }

    private static string MissingPermissions(PunChannelStartupStatus channel)
    {
        if (channel.CanView == false && channel.CanSend == false)
        {
            return "View Channel and Send Messages";
        }

        return channel.CanView == false ? "View Channel" : "Send Messages";
    }

    private static string UrlStatus(Uri? url)
        => url is null ? "missing." : "configured (reachability not checked).";
}
