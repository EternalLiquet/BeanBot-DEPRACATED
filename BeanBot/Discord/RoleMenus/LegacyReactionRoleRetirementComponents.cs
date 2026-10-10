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
            ? "The panel message or channel is gone. I'll remove its saved settings."
            : $"[Open legacy panel](https://discord.com/channels/{source.GuildId}/{source.ChannelId}/{source.MessageId})";
        var builder = new EmbedBuilder()
            .WithTitle("Retire legacy reaction-role panel?")
            .WithDescription(
                $"**{RoleMenuText.TruncateWithEllipsis(label, RoleMenuConstants.MaximumTitleLength)}**\n\n" +
                $"{sourceText}\n\n" +
                "I'll delete this panel and its saved settings. Members keep the roles they already have.")
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
                index == 0 ? "Roles and custom emoji" : "More mappings",
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
                "I removed the saved settings for that missing legacy panel.",
            LegacyReactionRoleRetirementStatus.Retired =>
                "I retired that legacy role panel and removed its saved settings.",
            LegacyReactionRoleRetirementStatus.StaleConfirmation =>
                "This confirmation has expired because the saved panel changed. Run `/role-menu retire-legacy` again.",
            LegacyReactionRoleRetirementStatus.AlreadyRetired =>
                "I couldn't find that saved legacy panel. Run `/role-menu retire-legacy` again to choose another.",
            LegacyReactionRoleRetirementStatus.AuthorizationDenied =>
                "I couldn't confirm you still have Manage Roles. Ask a server administrator to check your permission, then run `/role-menu retire-legacy` again.",
            LegacyReactionRoleRetirementStatus.InvalidSavedConfiguration =>
                "That saved panel no longer matches this server. Run `/role-menu retire-legacy` again.",
            LegacyReactionRoleRetirementStatus.UnsafeSource =>
                "I couldn't verify that this is my legacy role panel. Check the message and run `/role-menu retire-legacy` again.",
            LegacyReactionRoleRetirementStatus.PanelDeletionFailed =>
                "I couldn't delete that legacy panel. Its saved settings are still there. Check the panel and run `/role-menu retire-legacy` again.",
            LegacyReactionRoleRetirementStatus.PanelOutcomeUnknown =>
                "I couldn't confirm whether Discord deleted that panel. Its saved settings are still there. Check Discord before running `/role-menu retire-legacy` again.",
            LegacyReactionRoleRetirementStatus.PersistenceKept =>
                "The legacy panel is gone, but I couldn't remove its saved settings. Run `/role-menu retire-legacy` again to finish.",
            LegacyReactionRoleRetirementStatus.PersistenceOutcomeUnknown =>
                "The legacy panel is gone, but I couldn't confirm whether its saved settings were removed. Run `/role-menu retire-legacy` again to check.",
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
