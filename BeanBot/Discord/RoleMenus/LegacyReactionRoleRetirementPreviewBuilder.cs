using BeanBot.Persistence.Models;

namespace BeanBot.Discord.RoleMenus;

internal enum LegacyReactionRoleRetirementPreviewIssue
{
    MissingSettings,
    InvalidSavedBinding,
    UnsafePanel
}

internal sealed record LegacyReactionRoleRetirementPreviewResult(
    LegacyReactionRoleRetirementPreview? Preview,
    LegacyReactionRoleRetirementPreviewIssue? Issue);

internal static class LegacyReactionRoleRetirementPreviewBuilder
{
    internal static async Task<LegacyReactionRoleRetirementPreviewResult> CreateAsync(
        ulong messageId,
        ulong guildId,
        ulong botUserId,
        Func<ulong, CancellationToken, Task<ReactionRoleSettings?>> readSettings,
        Func<LegacyReactionRoleSource, ulong, CancellationToken,
            Task<LegacyReactionRolePanelLookupResult>> readPanel,
        Func<ulong, string?> roleName,
        Func<ulong, string?> customEmojiName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(readPanel);
        ArgumentNullException.ThrowIfNull(roleName);
        ArgumentNullException.ThrowIfNull(customEmojiName);

        var settings = await readSettings(messageId, cancellationToken);
        if (settings is null)
        {
            return new(null, LegacyReactionRoleRetirementPreviewIssue.MissingSettings);
        }
        if (!LegacyReactionRoleSourceParser.TryParse(settings, out var source)
            || source is null || source.GuildId != guildId
            || source.MessageId != messageId)
        {
            return new(null, LegacyReactionRoleRetirementPreviewIssue.InvalidSavedBinding);
        }

        var lookup = await readPanel(source, botUserId, cancellationToken);
        if (lookup.Status is LegacyReactionRolePanelLookupStatus.UnexpectedChannel
            or LegacyReactionRolePanelLookupStatus.Unrecognized)
        {
            return new(null, LegacyReactionRoleRetirementPreviewIssue.UnsafePanel);
        }

        var mappings = source.RoleIds.Zip(settings.RoleEmotePairs,
            (roleId, pair) => new LegacyReactionRoleRetirementMapping(
                roleId, pair.EmojiId,
                ulong.TryParse(pair.EmojiId, out var emojiId)
                    ? customEmojiName(emojiId) : null,
                roleName(roleId))).ToArray();
        return new(new LegacyReactionRoleRetirementPreview(
            source,
            lookup.SuggestedTitle,
            lookup.Status is LegacyReactionRolePanelLookupStatus.ChannelMissing
                or LegacyReactionRolePanelLookupStatus.MessageMissing,
            mappings,
            LegacyReactionRoleRetirementBinding.Fingerprint(settings)), null);
    }
}
