using BeanBot.Persistence.Models;
using Discord;
using MongoDB.Bson;

using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuSetupValidation;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuAuditStatus
{
    Healthy,
    Broken,
    Unknown
}

internal sealed record RoleMenuAuditItem(
    ObjectId MenuId,
    RoleMenuAuditStatus Status,
    string Reason);

internal sealed record RoleMenuAuditBatchResult(
    IReadOnlyList<RoleMenuAuditItem> Items,
    bool HasMore = false,
    bool RequestedMenuNotFound = false,
    bool PersistenceUnavailable = false);

internal sealed record RoleMenuAuditOperations(
    Func<ObjectId, ulong, CancellationToken, Task<RoleMenuSettings?>> ReadSettings,
    Func<ulong, int, CancellationToken, Task<IReadOnlyList<RoleMenuSettings>>> ReadGuildSettings,
    Func<ulong, ulong, CancellationToken, Task<IGuildUser?>> ReadBot,
    Func<IReadOnlyCollection<ulong>, IGuildUser, RoleMenuRoleValidationResult> ValidateRoles,
    Func<ulong, ulong, CancellationToken, Task<ITextChannel?>> ReadChannel,
    Func<IGuildUser, ITextChannel, string?> GetChannelPermissionFailure,
    Func<ITextChannel, ulong, ulong, ObjectId, CancellationToken, Task<RoleMenuPanelSnapshot?>> ReadPanel);

public sealed class RoleMenuAuditService
{
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);
    internal const int MaximumOwnedReads = 32;

    private readonly object _gate = new();
    private readonly HashSet<OwnedRead> _ownedReads = [];
    private readonly RoleMenuAuditOperations _operations;
    private readonly TimeSpan _lookupTimeout;
    private readonly int _maximumOwnedReads;
    private readonly Action<CancellationTokenSource, TimeSpan> _scheduleTimeout;
    private int _observedLateFaults;

    public RoleMenuAuditService(
        RoleMenuInteractionService roleMenus,
        DiscordRoleMenuClient discord)
        : this(new RoleMenuAuditOperations(
            roleMenus.GetAsync,
            async (guildId, maximumResults, cancellationToken) =>
                await roleMenus.GetByGuildAsync(guildId, maximumResults, cancellationToken),
            async (guildId, botUserId, cancellationToken) =>
                await discord.GetGuildUserAsync(
                    guildId,
                    botUserId,
                    CreateRequestOptions(cancellationToken)),
            ValidateRoles,
            async (guildId, channelId, cancellationToken) =>
                await discord.GetGuildTextChannelAsync(
                    guildId,
                    channelId,
                    CreateRequestOptions(cancellationToken)),
            GetChannelPermissionFailure,
            ReadPublicationPanelAsync))
    {
        ArgumentNullException.ThrowIfNull(roleMenus);
        ArgumentNullException.ThrowIfNull(discord);
    }

    internal RoleMenuAuditService(
        RoleMenuAuditOperations operations,
        TimeSpan? lookupTimeout = null,
        Action<CancellationTokenSource, TimeSpan>? scheduleTimeout = null,
        int maximumOwnedReads = MaximumOwnedReads)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _lookupTimeout = lookupTimeout ?? LookupTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_lookupTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOwnedReads, 1);
        _maximumOwnedReads = maximumOwnedReads;
        _scheduleTimeout = scheduleTimeout ?? ((cancellation, timeout) => cancellation.CancelAfter(timeout));
    }

    internal int OwnedReadCount
    {
        get
        {
            lock (_gate)
            {
                return _ownedReads.Count;
            }
        }
    }

    internal bool HasPendingOperations => OwnedReadCount > 0;

    internal int ObservedLateFaultCount => Volatile.Read(ref _observedLateFaults);

    internal async Task WaitForOwnedReadsAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] completions;
            lock (_gate)
            {
                if (_ownedReads.Count == 0)
                {
                    return;
                }

                completions = [.. _ownedReads.Select(read => read.Completion.Task)];
            }

            await Task.WhenAll(completions).WaitAsync(cancellationToken);
        }
    }

    internal async Task<RoleMenuAuditBatchResult> AuditAsync(
        ulong guildId,
        ulong botUserId,
        ObjectId? requestedMenuId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(guildId);
        ArgumentOutOfRangeException.ThrowIfZero(botUserId);

        IReadOnlyList<RoleMenuSettings> settings;
        if (requestedMenuId is { } menuId)
        {
            var read = await TryReadAsync(
                token => _operations.ReadSettings(menuId, guildId, token),
                cancellationToken);
            if (!read.Succeeded)
            {
                return new RoleMenuAuditBatchResult(
                    [Unknown(menuId, "I couldn't read this menu right now.")],
                    PersistenceUnavailable: true);
            }

            if (read.Value is null)
            {
                return new RoleMenuAuditBatchResult([], RequestedMenuNotFound: true);
            }

            settings = [read.Value];
        }
        else
        {
            var read = await TryReadAsync(
                token => _operations.ReadGuildSettings(
                    guildId,
                    RoleMenuConstants.MaximumListedMenus + 1,
                    token),
                cancellationToken);
            if (!read.Succeeded || read.Value is null)
            {
                return new RoleMenuAuditBatchResult([], PersistenceUnavailable: true);
            }

            settings = read.Value;
        }

        var hasMore = settings.Count > RoleMenuConstants.MaximumListedMenus;
        var boundedSettings = settings.Take(RoleMenuConstants.MaximumListedMenus).ToList();
        if (boundedSettings.Count == 0)
        {
            return new RoleMenuAuditBatchResult([], hasMore);
        }

        var botRead = await TryReadAsync(
            token => _operations.ReadBot(guildId, botUserId, token),
            cancellationToken);
        if (!botRead.Succeeded || botRead.Value is null)
        {
            return new RoleMenuAuditBatchResult(
                boundedSettings
                    .Select(menu => Unknown(
                        menu.Id,
                        "I couldn't check my current roles."))
                    .ToList(),
                hasMore);
        }

        var results = new List<RoleMenuAuditItem>(boundedSettings.Count);
        foreach (var menu in boundedSettings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await AuditOneAsync(
                menu,
                guildId,
                botRead.Value,
                cancellationToken));
        }

        return new RoleMenuAuditBatchResult(results, hasMore);
    }

    private async Task<RoleMenuAuditItem> AuditOneAsync(
        RoleMenuSettings settings,
        ulong guildId,
        IGuildUser bot,
        CancellationToken cancellationToken)
    {
        if (!RoleMenuSettingsParser.TryParse(settings, out var parsed, out _))
        {
            return Broken(settings.Id, "I can't read this menu's details.");
        }

        if (parsed.GuildId != guildId)
        {
            return Broken(settings.Id, "This menu points to another server.");
        }

        RoleMenuRoleValidationResult roleValidation;
        try
        {
            roleValidation = _operations.ValidateRoles(parsed.RoleIds, bot);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Unknown(settings.Id, "I couldn't check the menu's roles.");
        }

        if (!roleValidation.IsValid)
        {
            return Broken(settings.Id, FormatRoleIssue(roleValidation.Issues[0]));
        }

        var channelRead = await TryReadAsync(
            token => _operations.ReadChannel(guildId, parsed.ChannelId, token),
            cancellationToken);
        if (!channelRead.Succeeded)
        {
            return Unknown(settings.Id, "I couldn't check the menu's channel.");
        }

        if (channelRead.Value is null)
        {
            return Broken(settings.Id, "I couldn't find the menu's text channel.");
        }

        string? permissionFailure;
        try
        {
            permissionFailure = _operations.GetChannelPermissionFailure(bot, channelRead.Value);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Unknown(settings.Id, "I couldn't check permissions in the menu's channel.");
        }

        if (permissionFailure is not null)
        {
            return Broken(settings.Id, permissionFailure);
        }

        var panelRead = await TryReadAsync(
            token => _operations.ReadPanel(
                channelRead.Value,
                parsed.ChannelId,
                parsed.MessageId,
                settings.Id,
                token),
            cancellationToken);
        if (!panelRead.Succeeded)
        {
            return Unknown(settings.Id, "I couldn't check the menu message.");
        }

        if (panelRead.Value is null)
        {
            return Broken(settings.Id, "I couldn't find the menu message.");
        }

        var panelIssue = RoleMenuPanelContextValidator.Validate(
            parsed,
            guildId,
            panelRead.Value.ChannelId,
            panelRead.Value.MessageId,
            panelRead.Value.AuthorId,
            bot.Id,
            panelRead.Value.HasManageButton);
        if (panelIssue != RoleMenuPanelContextIssue.None)
        {
            return Broken(settings.Id, FormatPanelIssue(panelIssue));
        }

        return new RoleMenuAuditItem(
            settings.Id,
            RoleMenuAuditStatus.Healthy,
            "I checked the menu message, roles, and permissions.");
    }

    private async Task<ReadResult<T>> TryReadAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        OwnedRead? owner;
        lock (_gate)
        {
            owner = _ownedReads.Count < _maximumOwnedReads
                ? new OwnedRead(lookupCancellation)
                : null;
            if (owner is not null)
            {
                _ownedReads.Add(owner);
            }
        }

        if (owner is null)
        {
            lookupCancellation.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            return new ReadResult<T>(false, default);
        }

        try
        {
            _scheduleTimeout(lookupCancellation, _lookupTimeout);
            lookupCancellation.Token.ThrowIfCancellationRequested();
            var readTask = operation(lookupCancellation.Token)
                ?? throw new InvalidOperationException("An audit lookup returned no task.");
            Volatile.Write(ref owner.ReadTaskStarted, 1);
            _ = readTask.ContinueWith(
                completed => CompleteRead(owner, completed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try
            {
                var value = await readTask.WaitAsync(lookupCancellation.Token);
                return lookupCancellation.IsCancellationRequested
                    ? new ReadResult<T>(false, default)
                    : new ReadResult<T>(true, value);
            }
            catch
            {
                Volatile.Write(ref owner.WaiterDetached, 1);
                throw;
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && lookupCancellation.IsCancellationRequested)
        {
            return new ReadResult<T>(false, default);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReadResult<T>(false, default);
        }
        finally
        {
            if (owner.ReadTaskStarted == 0)
            {
                CompleteRead(owner, null);
            }

            owner.MarkWaiterDone();
        }
    }

    private void CompleteRead(OwnedRead owner, Task? completedTask)
    {
        if (completedTask?.IsFaulted == true)
        {
            _ = completedTask.Exception;
            if (Volatile.Read(ref owner.WaiterDetached) != 0)
            {
                Interlocked.Increment(ref _observedLateFaults);
            }
        }

        lock (_gate)
        {
            _ownedReads.Remove(owner);
        }

        owner.Completion.TrySetResult();
        owner.MarkReadDone();
    }

    private sealed class OwnedRead(CancellationTokenSource cancellation)
    {
        private int _readDone;
        private int _waiterDone;
        private int _disposed;

        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadTaskStarted;
        internal int WaiterDetached;

        internal void MarkReadDone()
        {
            Volatile.Write(ref _readDone, 1);
            TryDispose();
        }

        internal void MarkWaiterDone()
        {
            Volatile.Write(ref _waiterDone, 1);
            TryDispose();
        }

        private void TryDispose()
        {
            if (Volatile.Read(ref _readDone) != 0 &&
                Volatile.Read(ref _waiterDone) != 0 &&
                Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                cancellation.Dispose();
            }
        }
    }

    private static string FormatRoleIssue(RoleMenuRoleIssue issue)
        => issue.Kind switch
        {
            RoleMenuRoleIssueKind.BotMissingManageRoles =>
                "I need the Manage Roles permission to serve this menu.",
            RoleMenuRoleIssueKind.Missing => "One of this menu's roles was deleted.",
            RoleMenuRoleIssueKind.Everyone => "I can't give members the @everyone role.",
            RoleMenuRoleIssueKind.Managed => "I can't give members a role managed by Discord or an integration.",
            RoleMenuRoleIssueKind.BotHierarchy =>
                "One of this menu's roles is at or above my highest role.",
            RoleMenuRoleIssueKind.Duplicate => "This menu lists the same role twice.",
            _ => "I can't give members one of this menu's roles."
        };

    private static string FormatPanelIssue(RoleMenuPanelContextIssue issue)
        => issue switch
        {
            RoleMenuPanelContextIssue.GuildMismatch => "This menu message points to another server.",
            RoleMenuPanelContextIssue.ChannelMismatch => "This menu message points to another channel.",
            RoleMenuPanelContextIssue.MessageMismatch => "This menu points to another message.",
            RoleMenuPanelContextIssue.UnexpectedAuthor => "I didn't post this menu message.",
            RoleMenuPanelContextIssue.MissingManageButton =>
                "I couldn't find this menu's Manage Roles button on its message.",
            _ => "I couldn't match this message to the menu."
        };

    private static RoleMenuAuditItem Broken(ObjectId menuId, string reason)
        => new(menuId, RoleMenuAuditStatus.Broken, reason);

    private static RoleMenuAuditItem Unknown(ObjectId menuId, string reason)
        => new(menuId, RoleMenuAuditStatus.Unknown, reason);

    private readonly record struct ReadResult<T>(bool Succeeded, T? Value);
}

internal static class RoleMenuAuditPresentation
{
    internal static string Format(RoleMenuAuditBatchResult result, ObjectId? requestedMenuId)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RequestedMenuNotFound)
        {
            return "I couldn't find that role menu in this server. Check the ID in the audit result and try again.";
        }

        if (result.PersistenceUnavailable && result.Items.Count == 0)
        {
            return "**Unknown** — I couldn't check your role menus right now. " +
                   "Try `/role-menu audit` again in a moment.";
        }

        if (requestedMenuId is not null && result.Items.Count == 1)
        {
            var item = result.Items[0];
            var nextStep = item.Status switch
            {
                RoleMenuAuditStatus.Healthy => string.Empty,
                RoleMenuAuditStatus.Broken =>
                    "\nFix the issue, then run this audit again. If you no longer need this menu, " +
                    "run `/role-menu delete` and choose it from the list.",
                _ => "\nTry this audit again in a moment. Don't delete the menu based on this result."
            };
            return $"**{item.Status}** — `{item.MenuId}`\n{item.Reason}{nextStep}";
        }

        if (result.Items.Count == 0)
        {
            return "I couldn't find any role menus in this server.";
        }

        var healthy = result.Items.Count(item => item.Status == RoleMenuAuditStatus.Healthy);
        var broken = result.Items.Count(item => item.Status == RoleMenuAuditStatus.Broken);
        var unknown = result.Items.Count(item => item.Status == RoleMenuAuditStatus.Unknown);
        var menuNoun = result.Items.Count == 1 ? "role menu" : "role menus";
        var lines = new List<string>
        {
            $"I checked {result.Items.Count} {menuNoun}: **{healthy} Healthy**, " +
            $"**{broken} Broken**, **{unknown} Unknown**."
        };
        if (broken > 0)
        {
            lines.Add(broken == 1
                ? "Fix the broken menu and audit it again."
                : "Fix the broken menus and audit them again.");
        }

        if (unknown > 0)
        {
            lines.Add(unknown == 1
                ? "Try the unknown menu again later."
                : "Try the unknown menus again later.");
        }

        foreach (var item in result.Items)
        {
            var reason = item.Status == RoleMenuAuditStatus.Healthy
                ? string.Empty
                : " — " + RoleMenuText.TruncateWithEllipsis(item.Reason, 32);
            lines.Add($"{item.Status} `{item.MenuId}`{reason}");
        }

        if (result.HasMore)
        {
            lines.Add(
                "I checked only the 25 newest menus. If you have an older menu's ID, " +
                "run `/role-menu audit menu-id:<id>`.");
        }

        return RoleMenuPresentation.BoundResponseContent(string.Join('\n', lines));
    }
}
