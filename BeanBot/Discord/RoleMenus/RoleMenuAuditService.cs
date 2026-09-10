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

    private readonly RoleMenuAuditOperations _operations;

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

    internal RoleMenuAuditService(RoleMenuAuditOperations operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
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
                    [Unknown(menuId, "Saved configuration could not be read.")],
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
                        "Bean Bot's current server role state could not be verified."))
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
        if (!RoleMenuSettingsParser.TryParse(settings, out var parsed, out var settingsIssue))
        {
            return Broken(
                settings.Id,
                $"Saved configuration is invalid ({settingsIssue}).");
        }

        if (parsed.GuildId != guildId)
        {
            return Broken(settings.Id, "Saved configuration belongs to a different server.");
        }

        RoleMenuRoleValidationResult roleValidation;
        try
        {
            roleValidation = _operations.ValidateRoles(parsed.RoleIds, bot);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Unknown(settings.Id, "Current role state could not be verified.");
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
            return Unknown(settings.Id, "Target channel state could not be verified.");
        }

        if (channelRead.Value is null)
        {
            return Broken(settings.Id, "Target text channel is missing or no longer valid.");
        }

        string? permissionFailure;
        try
        {
            permissionFailure = _operations.GetChannelPermissionFailure(bot, channelRead.Value);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Unknown(settings.Id, "Target channel permissions could not be verified.");
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
            return Unknown(settings.Id, "Published panel state could not be verified.");
        }

        if (panelRead.Value is null)
        {
            return Broken(settings.Id, "Published panel message is missing.");
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
            "Panel, roles, and required permissions were verified.");
    }

    private static async Task<ReadResult<T>> TryReadAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookupCancellation.CancelAfter(LookupTimeout);
        try
        {
            return new ReadResult<T>(true, await operation(lookupCancellation.Token));
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
    }

    private static string FormatRoleIssue(RoleMenuRoleIssue issue)
        => issue.Kind switch
        {
            RoleMenuRoleIssueKind.BotMissingManageRoles =>
                "Bean Bot no longer has the Manage Roles permission.",
            RoleMenuRoleIssueKind.Missing => "A configured role was deleted.",
            RoleMenuRoleIssueKind.Everyone => "The menu contains the @everyone role.",
            RoleMenuRoleIssueKind.Managed => "A configured role is now managed by Discord or an integration.",
            RoleMenuRoleIssueKind.BotHierarchy =>
                "A configured role is now at or above Bean Bot's highest role.",
            RoleMenuRoleIssueKind.Duplicate => "The saved menu contains a duplicate role.",
            _ => "A configured role is no longer assignable by Bean Bot."
        };

    private static string FormatPanelIssue(RoleMenuPanelContextIssue issue)
        => issue switch
        {
            RoleMenuPanelContextIssue.GuildMismatch => "Published panel points at the wrong server.",
            RoleMenuPanelContextIssue.ChannelMismatch => "Published panel points at the wrong channel.",
            RoleMenuPanelContextIssue.MessageMismatch => "Published panel points at the wrong message.",
            RoleMenuPanelContextIssue.UnexpectedAuthor => "Referenced message was not authored by Bean Bot.",
            RoleMenuPanelContextIssue.MissingManageButton =>
                "Referenced message no longer contains this menu's Manage Roles button.",
            _ => "Published panel identity is invalid."
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
            return "No saved role menu with that ID exists in this server.";
        }

        if (result.PersistenceUnavailable && result.Items.Count == 0)
        {
            return "**Unknown** — Bean Bot could not read the saved role-menu configuration. " +
                   "No Discord state was changed.";
        }

        if (requestedMenuId is not null && result.Items.Count == 1)
        {
            var item = result.Items[0];
            return $"**{item.Status}** — `{item.MenuId}`\n{item.Reason}\n\n" +
                   "Audit is read-only; no Discord or saved role-menu state was changed.";
        }

        if (result.Items.Count == 0)
        {
            return "This server has no saved dropdown role menus.";
        }

        var healthy = result.Items.Count(item => item.Status == RoleMenuAuditStatus.Healthy);
        var broken = result.Items.Count(item => item.Status == RoleMenuAuditStatus.Broken);
        var unknown = result.Items.Count(item => item.Status == RoleMenuAuditStatus.Unknown);
        var lines = new List<string>
        {
            $"Role-menu audit: **{healthy} Healthy**, **{broken} Broken**, **{unknown} Unknown**."
        };
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
                "Only the 25 newest menus were audited. Audit an older panel by passing its footer ID.");
        }

        lines.Add("Read-only audit: no Discord or saved state was changed.");
        return RoleMenuPresentation.BoundResponseContent(string.Join('\n', lines));
    }
}
