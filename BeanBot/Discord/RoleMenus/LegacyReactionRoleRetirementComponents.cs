using System.Globalization;
using Discord;

namespace BeanBot.Discord.RoleMenus;

internal sealed record LegacyReactionRoleRetirementMapping(
    ulong RoleId,
    string EmojiId);

internal sealed record LegacyReactionRoleRetirementPreview(
    LegacyReactionRoleSource Source,
    string? Label,
    bool SourceWasMissing,
    IReadOnlyList<LegacyReactionRoleRetirementMapping> Mappings);

internal static class LegacyReactionRoleRetirementCustomIds
{
    internal const string ConfirmPattern = "role-menu:retire-legacy-confirm:*:*";
    internal const string CancelPattern = "role-menu:retire-legacy-cancel:*:*";

    internal static string Confirm(ulong userId, ulong messageId)
        => EnsureValid(
            $"role-menu:retire-legacy-confirm:{userId.ToString(CultureInfo.InvariantCulture)}:" +
            messageId.ToString(CultureInfo.InvariantCulture));

    internal static string Cancel(ulong userId, ulong messageId)
        => EnsureValid(
            $"role-menu:retire-legacy-cancel:{userId.ToString(CultureInfo.InvariantCulture)}:" +
            messageId.ToString(CultureInfo.InvariantCulture));

    private static string EnsureValid(string customId)
    {
        if (customId.Length > ComponentBuilder.MaxCustomIdLength)
        {
            throw new InvalidOperationException(
                $"Legacy role retirement custom ID exceeded {ComponentBuilder.MaxCustomIdLength} characters.");
        }

        return customId;
    }
}

internal static class LegacyReactionRoleRetirementComponents
{
    private const int MappingsPerField = 5;

    internal static Embed BuildConfirmationEmbed(LegacyReactionRoleRetirementPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var source = preview.Source;
        var label = string.IsNullOrWhiteSpace(preview.Label)
            ? "Unlabeled legacy panel"
            : preview.Label;
        var sourceText = preview.SourceWasMissing
            ? "The saved channel/message is already missing. Confirming removes only the stale saved configuration."
            : $"[Open legacy panel](https://discord.com/channels/{source.GuildId}/{source.ChannelId}/{source.MessageId})";
        var builder = new EmbedBuilder()
            .WithTitle("Retire legacy reaction-role panel?")
            .WithDescription(
                $"**{RoleMenuText.TruncateWithEllipsis(label, RoleMenuConstants.MaximumTitleLength)}**\n\n" +
                $"{sourceText}\n\n" +
                "Bean Bot will revalidate the source, delete the matching legacy panel first, then remove its saved configuration. Existing member roles are not changed.")
            .WithColor(Color.Red);

        for (var index = 0; index < preview.Mappings.Count; index += MappingsPerField)
        {
            var value = string.Join(
                "\n",
                preview.Mappings
                    .Skip(index)
                    .Take(MappingsPerField)
                    .Select(FormatMapping));
            builder.AddField(
                index == 0 ? "Configured role / emote mappings" : "More mappings",
                value);
        }

        return builder.Build();
    }

    internal static MessageComponent BuildConfirmationComponents(ulong userId, ulong messageId)
        => new ComponentBuilder()
            .WithButton(
                "Retire legacy panel",
                LegacyReactionRoleRetirementCustomIds.Confirm(userId, messageId),
                ButtonStyle.Danger)
            .WithButton(
                "Cancel",
                LegacyReactionRoleRetirementCustomIds.Cancel(userId, messageId),
                ButtonStyle.Secondary)
            .Build();

    internal static string FormatResult(LegacyReactionRoleRetirementResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            LegacyReactionRoleRetirementStatus.Retired when result.SourceWasMissing =>
                "The legacy panel was already missing. Bean Bot removed its stale saved configuration. Existing member roles were not changed.",
            LegacyReactionRoleRetirementStatus.Retired =>
                "Legacy reaction-role panel retired. The matching panel and saved configuration are gone. Existing member roles were not changed.",
            LegacyReactionRoleRetirementStatus.AlreadyRetired =>
                "No saved legacy reaction-role configuration exists for that message. Nothing was changed.",
            LegacyReactionRoleRetirementStatus.AuthorizationDenied =>
                "Bean Bot could not confirm that you still have Manage Roles. Nothing was changed.",
            LegacyReactionRoleRetirementStatus.InvalidSavedConfiguration =>
                "The saved legacy configuration no longer matches this server and message. Nothing was deleted.",
            LegacyReactionRoleRetirementStatus.UnsafeSource =>
                "The saved message could not be positively identified as the expected Bean Bot legacy role panel. Nothing was deleted.",
            LegacyReactionRoleRetirementStatus.PanelDeletionFailed =>
                "Bean Bot confirmed the legacy panel still exists or changed before deletion. Its saved configuration was kept; inspect the source and retry after correcting it.",
            LegacyReactionRoleRetirementStatus.PanelOutcomeUnknown =>
                "Bean Bot could not confirm whether Discord deleted the legacy panel. Saved configuration was kept and no automatic retry was attempted. Inspect the source before retrying.",
            LegacyReactionRoleRetirementStatus.PersistenceKept =>
                "The legacy Discord panel is gone, but its saved configuration could not be removed. Run the retirement command again to finish stale-state cleanup. Existing member roles were not changed.",
            LegacyReactionRoleRetirementStatus.PersistenceOutcomeUnknown =>
                "The legacy Discord panel is gone, but Bean Bot could not confirm whether the saved configuration was removed. Run the retirement command again to reconcile the exact source record.",
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, null)
        };
    }

    private static string FormatMapping(LegacyReactionRoleRetirementMapping mapping)
    {
        var emoji = ulong.TryParse(
            mapping.EmojiId,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var emojiId)
            ? emojiId.ToString(CultureInfo.InvariantCulture)
            : "invalid saved emote ID";
        return $"`{emoji}` → <@&{mapping.RoleId.ToString(CultureInfo.InvariantCulture)}>";
    }
}
