using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BeanBot.Persistence.Models;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuSettingsIssue
{
    None,
    InvalidGuild,
    InvalidChannel,
    InvalidMessage,
    InvalidTitle,
    InvalidDescription,
    InvalidSelectionMode,
    InvalidRoleCount,
    InvalidRoleId,
    DuplicateRoleId
}

internal sealed record ParsedRoleMenuSettings(
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    IReadOnlyList<ulong> RoleIds);

internal static class RoleMenuSettingsParser
{
    internal static bool TryParse(
        RoleMenuSettings settings,
        [NotNullWhen(true)] out ParsedRoleMenuSettings? parsed,
        out RoleMenuSettingsIssue issue)
    {
        ArgumentNullException.ThrowIfNull(settings);
        parsed = null;

        if (!TryParseSnowflake(settings.GuildId, out var guildId))
        {
            issue = RoleMenuSettingsIssue.InvalidGuild;
            return false;
        }

        if (!TryParseSnowflake(settings.ChannelId, out var channelId))
        {
            issue = RoleMenuSettingsIssue.InvalidChannel;
            return false;
        }

        if (!TryParseSnowflake(settings.MessageId, out var messageId))
        {
            issue = RoleMenuSettingsIssue.InvalidMessage;
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.Title)
            || settings.Title.Length > RoleMenuConstants.MaximumTitleLength)
        {
            issue = RoleMenuSettingsIssue.InvalidTitle;
            return false;
        }

        if (settings.Description.Length > RoleMenuConstants.MaximumDescriptionLength)
        {
            issue = RoleMenuSettingsIssue.InvalidDescription;
            return false;
        }

        if (!Enum.IsDefined(settings.SelectionMode))
        {
            issue = RoleMenuSettingsIssue.InvalidSelectionMode;
            return false;
        }

        if (settings.RoleIds.Count is < 1 or > RoleMenuConstants.MaximumRoles)
        {
            issue = RoleMenuSettingsIssue.InvalidRoleCount;
            return false;
        }

        var roleIds = new List<ulong>(settings.RoleIds.Count);
        var uniqueRoleIds = new HashSet<ulong>();
        foreach (var roleIdText in settings.RoleIds)
        {
            if (!TryParseSnowflake(roleIdText, out var roleId))
            {
                issue = RoleMenuSettingsIssue.InvalidRoleId;
                return false;
            }

            if (!uniqueRoleIds.Add(roleId))
            {
                issue = RoleMenuSettingsIssue.DuplicateRoleId;
                return false;
            }

            roleIds.Add(roleId);
        }

        parsed = new ParsedRoleMenuSettings(guildId, channelId, messageId, roleIds);
        issue = RoleMenuSettingsIssue.None;
        return true;
    }

    private static bool TryParseSnowflake(string value, out ulong snowflake)
        => ulong.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out snowflake)
            && snowflake != 0;
}
