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

internal sealed class BeanBotStartupReport
{
    private readonly Func<PunChannelStartupStatus> _checkChannel;
    private readonly string _version;
    private readonly Uri? _hatoeteUrl;
    private readonly Uri? _yoshimaruUrl;
    private readonly Action<bool> _recordChannelStatus;
    private readonly IOwnerErrorNotifier _notifier;
    private readonly ILogger<BeanBotStartupReport> _logger;
    private int _queued;

    public BeanBotStartupReport(
        DiscordSocketClient discordClient,
        BeanBotOptions options,
        DailyPunService dailyPunService,
        DiscordOwnerErrorNotifier notifier,
        ILogger<BeanBotStartupReport> logger)
        : this(
            () => CheckChannel(discordClient, options.GeneralChannelId),
            BuildIdentity.Current.Version,
            options.HatoeteImageUrl,
            options.YoshimaruImageUrl,
            dailyPunService.RecordStartupChannelStatus,
            notifier,
            logger)
    {
    }

    internal BeanBotStartupReport(
        Func<PunChannelStartupStatus> checkChannel,
        string version,
        Uri? hatoeteUrl,
        Uri? yoshimaruUrl,
        Action<bool> recordChannelStatus,
        IOwnerErrorNotifier notifier,
        ILogger<BeanBotStartupReport>? logger = null)
    {
        _checkChannel = checkChannel ?? throw new ArgumentNullException(nameof(checkChannel));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _hatoeteUrl = hatoeteUrl;
        _yoshimaruUrl = yoshimaruUrl;
        _recordChannelStatus = recordChannelStatus
            ?? throw new ArgumentNullException(nameof(recordChannelStatus));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger ?? NullLogger<BeanBotStartupReport>.Instance;
    }

    internal void QueueOnFirstReady()
    {
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0)
        {
            return;
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
        _recordChannelStatus(channel.State is
            PunChannelStartupState.NotFound or PunChannelStartupState.MissingPermission);
        _notifier.Enqueue(Format(_version, channel, _hatoeteUrl, _yoshimaruUrl));
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
