using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BeanBot.Persistence.Models;
using Discord;

namespace BeanBot.Discord.RoleMenus;

internal sealed record LegacyReactionRoleRetirementMapping(
    ulong RoleId,
    string EmojiId,
    string? EmojiName = null,
    string? RoleName = null);

internal sealed record LegacyReactionRoleRetirementPreview(
    LegacyReactionRoleSource Source,
    string? Label,
    bool SourceWasMissing,
    IReadOnlyList<LegacyReactionRoleRetirementMapping> Mappings,
    string Fingerprint = "");

internal static class LegacyReactionRoleRetirementBinding
{
    internal static string Fingerprint(ReactionRoleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var value = settings.Id + "|" + settings.GuildId + "|" + settings.ChannelId
            + "|" + settings.MessageId + "|" + string.Join("|",
                settings.RoleEmotePairs.Select(pair => pair.RoleId + ":" + pair.EmojiId));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }

    internal static bool Matches(ReactionRoleSettings settings, string fingerprint)
        => string.Equals(Fingerprint(settings), fingerprint, StringComparison.Ordinal);
}

internal static class LegacyReactionRoleRetirementCustomIds
{
    internal const string ConfirmPattern = "rm:lc:*:*:*:*";
    internal const string CancelPattern = "rm:lx:*:*:*:*";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal static string Confirm(ulong userId, ulong messageId, long expiresUnixSeconds,
        string fingerprint)
        => EnsureValid($"rm:lc:{userId}:{messageId}:{expiresUnixSeconds}:{fingerprint}");

    internal static string Cancel(ulong userId, ulong messageId, long expiresUnixSeconds,
        string fingerprint)
        => EnsureValid($"rm:lx:{userId}:{messageId}:{expiresUnixSeconds}:{fingerprint}");

    internal static bool IsCurrent(long expiresUnixSeconds, DateTimeOffset now)
        => expiresUnixSeconds > now.ToUnixTimeSeconds()
            && expiresUnixSeconds <= now.Add(Lifetime).ToUnixTimeSeconds();

    private static string EnsureValid(string customId)
    {
        if (customId.Length > ComponentBuilder.MaxCustomIdLength)
        {
            throw new InvalidOperationException("Legacy retirement control is too long.");
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

    internal static MessageComponent BuildConfirmationComponents(
        ulong userId, ulong messageId, long expiresUnixSeconds, string fingerprint)
        => new ComponentBuilder()
            .WithButton(
                "Retire legacy panel",
                LegacyReactionRoleRetirementCustomIds.Confirm(userId, messageId, expiresUnixSeconds, fingerprint),
                ButtonStyle.Danger)
            .WithButton(
                "Cancel",
                LegacyReactionRoleRetirementCustomIds.Cancel(userId, messageId, expiresUnixSeconds, fingerprint),
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
            LegacyReactionRoleRetirementStatus.StaleConfirmation =>
                "This confirmation has expired because the saved panel changed. Run `/role-menu retire-legacy` again.",
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
        var emoji = string.IsNullOrWhiteSpace(mapping.EmojiName)
            ? "Custom emoji unavailable"
            : $":{RoleMenuText.TruncateWithEllipsis(mapping.EmojiName, 40)}:";
        var role = string.IsNullOrWhiteSpace(mapping.RoleName)
            ? "Deleted role"
            : RoleMenuText.TruncateWithEllipsis(mapping.RoleName, 60);
        return $"{emoji} → {role}";
    }
}
