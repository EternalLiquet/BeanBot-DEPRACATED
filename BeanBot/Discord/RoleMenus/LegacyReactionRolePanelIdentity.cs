using System.Globalization;
using Discord;

namespace BeanBot.Discord.RoleMenus;

internal static class LegacyReactionRolePanelIdentity
{
    private const string FooterPrefix = "Role Group: ";

    internal static bool TryRecognize(
        ulong authorId,
        ulong expectedBotUserId,
        IReadOnlyCollection<IEmbed> embeds,
        IReadOnlyCollection<ulong> expectedRoleIds,
        out string? suggestedTitle)
    {
        suggestedTitle = null;
        if (authorId == 0
            || authorId != expectedBotUserId
            || embeds.Count != 1
            || expectedRoleIds.Count == 0
            || expectedRoleIds.Distinct().Count() != expectedRoleIds.Count)
        {
            return false;
        }

        var embed = embeds.Single();
        var footer = embed.Footer?.Text;
        if (string.IsNullOrWhiteSpace(footer)
            || !footer.StartsWith(FooterPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedMentions = expectedRoleIds
            .Select(roleId => $"<@&{roleId.ToString(CultureInfo.InvariantCulture)}>")
            .ToHashSet(StringComparer.Ordinal);
        var actualMentions = embed.Fields
            .Select(field => field.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        if (embed.Fields.Count() != expectedMentions.Count
            || !actualMentions.SetEquals(expectedMentions))
        {
            return false;
        }

        var label = footer[FooterPrefix.Length..].Trim();
        suggestedTitle = string.IsNullOrWhiteSpace(label) ? null : label;
        return true;
    }
}
