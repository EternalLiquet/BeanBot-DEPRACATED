using System.Globalization;
using BeanBot.Persistence.Models;
using Discord;

namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuPanelIdentity
{
    internal static bool Matches(
        RoleMenuSettings? settings,
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong authorId,
        ulong botUserId,
        IMessage message)
    {
        if (settings is null
            || authorId != botUserId
            || !RoleMenuSettingsParser.TryParse(settings, out var parsed, out _)
            || parsed.GuildId != guildId
            || parsed.ChannelId != channelId
            || parsed.MessageId != messageId)
        {
            return false;
        }

        return RoleMenuComponents.HasManageButton(message, settings.Id);
    }

    internal static bool MatchesConfirmation(
        RoleMenuSettings? settings,
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong botUserId,
        RoleMenuPanelLookupResult lookup)
    {
        if (settings is null
            || settings.GuildId != guildId.ToString(CultureInfo.InvariantCulture)
            || settings.ChannelId != channelId.ToString(CultureInfo.InvariantCulture)
            || settings.MessageId != messageId.ToString(CultureInfo.InvariantCulture)
            || lookup.Status != RoleMenuPanelLookupStatus.Found
            || lookup.Panel is not { } panel)
        {
            return false;
        }

        return panel.GuildId == guildId
            && panel.ChannelId == channelId
            && panel.MessageId == messageId
            && panel.AuthorId == botUserId
            && panel.HasManageButton;
    }
}
