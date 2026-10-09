using System.Globalization;
using BeanBot.Persistence.Models;

namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuPublicationSettings
{
    internal static RoleMenuSettings Create(
        RoleMenuDraft draft,
        ulong messageId,
        DateTime existingCreatedAtUtc = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var settings = new RoleMenuSettings(
            draft.MenuId,
            draft.GuildId.ToString(CultureInfo.InvariantCulture),
            draft.TargetChannelId.ToString(CultureInfo.InvariantCulture),
            messageId.ToString(CultureInfo.InvariantCulture),
            draft.Title,
            draft.Description,
            draft.RoleIds.Select(roleId => roleId.ToString(CultureInfo.InvariantCulture)),
            draft.SelectionMode);
        settings.CreatedAtUtc = existingCreatedAtUtc;
        return settings;
    }

    internal static bool Matches(
        RoleMenuSettings? settings,
        RoleMenuDraft draft,
        ulong messageId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return settings is not null
               && settings.Id == draft.MenuId
               && string.Equals(
                   settings.GuildId,
                   draft.GuildId.ToString(CultureInfo.InvariantCulture),
                   StringComparison.Ordinal)
               && string.Equals(
                   settings.ChannelId,
                   draft.TargetChannelId.ToString(CultureInfo.InvariantCulture),
                   StringComparison.Ordinal)
               && string.Equals(
                   settings.MessageId,
                   messageId.ToString(CultureInfo.InvariantCulture),
                   StringComparison.Ordinal)
               && string.Equals(settings.Title, draft.Title, StringComparison.Ordinal)
               && string.Equals(settings.Description, draft.Description, StringComparison.Ordinal)
               && settings.SelectionMode == draft.SelectionMode
               && settings.RoleIds.SequenceEqual(
                   draft.RoleIds.Select(roleId => roleId.ToString(CultureInfo.InvariantCulture)),
                   StringComparer.Ordinal);
    }
}
