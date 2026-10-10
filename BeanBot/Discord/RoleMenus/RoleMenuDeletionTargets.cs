using System.Globalization;
using System.Net;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.Net;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

/// <summary>What an administrator can safely do with a saved menu's published message.</summary>
internal enum RoleMenuPanelState
{
    Current,
    MessageMissing,
    ChannelMissing,
    NotAPanel,
    Inaccessible,
    Unavailable
}

internal enum RoleMenuMessageTargetIssue
{
    None,
    WrongLocation,
    NotBotMessage,
    NotRoleMenu,
    NotSaved
}

internal sealed record RoleMenuDeletionPage(
    IReadOnlyList<RoleMenuSettings> Menus,
    RoleMenuPageCursor? Previous,
    RoleMenuPageCursor? Next);

internal static class RoleMenuDeletionTargets
{
    internal const int MaximumMessageMatches = 5;

    internal static RoleMenuDeletionPage BuildPage(
        IReadOnlyList<RoleMenuSettings> fetched,
        RoleMenuPageCursor? cursor,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var hasExtra = fetched.Count > pageSize;
        var readingNewer = cursor is { Direction: RoleMenuPageDirection.Newer };

        // Pages are newest first. Reading newer menus fetches one extra at the newest end.
        List<RoleMenuSettings> menus = readingNewer
            ? [.. fetched.Skip(Math.Max(0, fetched.Count - pageSize))]
            : [.. fetched.Take(pageSize)];
        if (menus.Count == 0)
        {
            return new RoleMenuDeletionPage(menus, null, null);
        }

        var hasNewer = readingNewer ? hasExtra : cursor is not null;
        var hasOlder = readingNewer || hasExtra;
        return new RoleMenuDeletionPage(
            menus,
            hasNewer ? CreateCursor(menus[0], RoleMenuPageDirection.Newer) : null,
            hasOlder ? CreateCursor(menus[^1], RoleMenuPageDirection.Older) : null);
    }

    internal static RoleMenuPageCursor CreateCursor(
        RoleMenuSettings settings,
        RoleMenuPageDirection direction)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new RoleMenuPageCursor(settings.CreatedAtUtc, settings.Id, direction);
    }

    /// <summary>
    /// Checks the message an administrator picked before any saved data is read. It must be a
    /// BeanBot message in the channel the command came from and carry a role-menu button.
    /// </summary>
    internal static RoleMenuMessageTargetIssue CheckMessage(
        ulong interactionChannelId,
        ulong botUserId,
        ulong messageChannelId,
        ulong messageAuthorId,
        IReadOnlyCollection<ObjectId> manageButtonMenuIds)
    {
        ArgumentNullException.ThrowIfNull(manageButtonMenuIds);
        if (messageChannelId == 0 || messageChannelId != interactionChannelId)
        {
            return RoleMenuMessageTargetIssue.WrongLocation;
        }

        if (messageAuthorId != botUserId)
        {
            return RoleMenuMessageTargetIssue.NotBotMessage;
        }

        return manageButtonMenuIds.Count == 0
            ? RoleMenuMessageTargetIssue.NotRoleMenu
            : RoleMenuMessageTargetIssue.None;
    }

    /// <summary>
    /// Picks the saved menu for a message. The record must point at this exact guild, channel and
    /// message, and the message's own button must name the same menu. Anything else is unsaved.
    /// </summary>
    internal static RoleMenuSettings? MatchSavedMenu(
        IReadOnlyCollection<RoleMenuSettings> savedMenus,
        IReadOnlyCollection<ObjectId> manageButtonMenuIds,
        ulong guildId,
        ulong channelId,
        ulong messageId)
    {
        ArgumentNullException.ThrowIfNull(savedMenus);
        ArgumentNullException.ThrowIfNull(manageButtonMenuIds);
        var matches = savedMenus
            .Where(menu => manageButtonMenuIds.Contains(menu.Id)
                           && IsSnowflake(menu.GuildId, guildId)
                           && IsSnowflake(menu.ChannelId, channelId)
                           && IsSnowflake(menu.MessageId, messageId))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    internal static IReadOnlyList<ObjectId> GetManageButtonMenuIds(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Components
            .OfType<ActionRowComponent>()
            .SelectMany(row => row.Components)
            .OfType<ButtonComponent>()
            .Select(button => RoleMenuCustomIds.TryParseManage(button.CustomId, out var menuId)
                ? menuId
                : ObjectId.Empty)
            .Where(menuId => menuId != ObjectId.Empty)
            .Distinct()
            .ToList();
    }

    internal static RoleMenuPanelState ClassifyPanel(
        RoleMenuPanelLookupResult lookup,
        RoleMenuSettings settings,
        ulong guildId,
        ulong botUserId)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(settings);
        return lookup.Status switch
        {
            RoleMenuPanelLookupStatus.ChannelMissing => RoleMenuPanelState.ChannelMissing,
            RoleMenuPanelLookupStatus.MessageMissing => RoleMenuPanelState.MessageMissing,
            RoleMenuPanelLookupStatus.Found when lookup.Panel is { } panel
                                                 && panel.GuildId == guildId
                                                 && IsSnowflake(settings.ChannelId, panel.ChannelId)
                                                 && IsSnowflake(settings.MessageId, panel.MessageId)
                                                 && panel.AuthorId == botUserId
                                                 && panel.HasManageButton
                => RoleMenuPanelState.Current,
            _ => RoleMenuPanelState.NotAPanel
        };
    }

    /// <summary>
    /// A permission error proves only that BeanBot can't see the message, not that it is gone.
    /// </summary>
    internal static RoleMenuPanelState ClassifyPanelFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is HttpException
        {
            HttpCode: HttpStatusCode.Forbidden
        } or HttpException
        {
            DiscordCode: DiscordErrorCode.MissingPermissions or DiscordErrorCode.InsufficientPermissions
        }
            ? RoleMenuPanelState.Inaccessible
            : RoleMenuPanelState.Unavailable;
    }

    internal static bool CanDelete(RoleMenuPanelState state)
        => state is not (RoleMenuPanelState.Inaccessible or RoleMenuPanelState.Unavailable);

    internal static bool IsControlExpired(DateTimeOffset controlCreatedAt, DateTimeOffset now)
        => now - controlCreatedAt > RoleMenuConstants.ManagementControlLifetime;

    internal static long GetVersion(RoleMenuSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.UpdatedAtUtc.Ticks;
    }

    private static bool IsSnowflake(string value, ulong expected)
        => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
           && parsed != 0
           && parsed == expected;
}
