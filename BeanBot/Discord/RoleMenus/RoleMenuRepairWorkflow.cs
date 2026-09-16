using BeanBot.Persistence.Models;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuRepairInspectionStatus
{
    Eligible,
    SettingsMissing,
    SettingsInvalid,
    Healthy,
    UnexpectedPanel
}

internal enum RoleMenuRepairPanelIssue
{
    None,
    ChannelMissing,
    MessageMissing,
    UnexpectedChannelType,
    GuildMismatch,
    ChannelMismatch,
    MessageMismatch,
    UnexpectedAuthor,
    MissingManageButton,
    InvalidLookup
}

internal sealed record RoleMenuRepairInspectionResult(
    RoleMenuRepairInspectionStatus Status,
    RoleMenuSettings? Settings = null,
    ParsedRoleMenuSettings? ParsedSettings = null,
    RoleMenuRepairPanelIssue PanelIssue = RoleMenuRepairPanelIssue.None)
{
    internal bool IsEligible => Status == RoleMenuRepairInspectionStatus.Eligible;
}

internal static class RoleMenuRepairWorkflow
{
    internal static async Task<RoleMenuRepairInspectionResult> InspectAsync(
        ObjectId menuId,
        ulong guildId,
        ulong botUserId,
        Func<ObjectId, ulong, CancellationToken, Task<RoleMenuSettings?>> readSettings,
        Func<ulong, ObjectId, ulong, ulong, CancellationToken, Task<RoleMenuPanelLookupResult>>
            readPanel,
        CancellationToken cancellationToken)
    {
        if (menuId == ObjectId.Empty)
        {
            throw new ArgumentException("A role menu ID is required.", nameof(menuId));
        }

        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(readPanel);

        var settings = await readSettings(menuId, guildId, cancellationToken);
        if (settings is null)
        {
            return new RoleMenuRepairInspectionResult(
                RoleMenuRepairInspectionStatus.SettingsMissing);
        }

        if (!RoleMenuSettingsParser.TryParse(settings, out var parsed, out _)
            || parsed.GuildId != guildId)
        {
            return new RoleMenuRepairInspectionResult(
                RoleMenuRepairInspectionStatus.SettingsInvalid,
                settings);
        }

        var lookup = await readPanel(
            guildId,
            settings.Id,
            parsed.ChannelId,
            parsed.MessageId,
            cancellationToken);
        return InspectLookup(settings, parsed, lookup, guildId, botUserId);
    }

    internal static RoleMenuDraft CreateRepairDraft(
        RoleMenuSettings settings,
        ParsedRoleMenuSettings parsed,
        ulong administratorId,
        ulong targetChannelId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(parsed);
        if (targetChannelId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetChannelId));
        }

        return new RoleMenuDraft(
            Guid.NewGuid(),
            settings.Id,
            parsed.GuildId,
            administratorId,
            targetChannelId,
            settings.Title,
            settings.Description,
            [.. parsed.RoleIds],
            settings.SelectionMode,
            DateTimeOffset.UtcNow.Add(RoleMenuConstants.DraftLifetime));
    }

    internal static bool HasSameSavedConfiguration(
        RoleMenuSettings first,
        RoleMenuSettings second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.Id == second.Id
               && string.Equals(first.GuildId, second.GuildId, StringComparison.Ordinal)
               && string.Equals(first.ChannelId, second.ChannelId, StringComparison.Ordinal)
               && string.Equals(first.MessageId, second.MessageId, StringComparison.Ordinal)
               && string.Equals(first.Title, second.Title, StringComparison.Ordinal)
               && string.Equals(first.Description, second.Description, StringComparison.Ordinal)
               && first.SelectionMode == second.SelectionMode
               && first.RoleIds.SequenceEqual(second.RoleIds, StringComparer.Ordinal);
    }

    private static RoleMenuRepairInspectionResult InspectLookup(
        RoleMenuSettings settings,
        ParsedRoleMenuSettings parsed,
        RoleMenuPanelLookupResult lookup,
        ulong guildId,
        ulong botUserId)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (lookup.Status == RoleMenuPanelLookupStatus.ChannelMissing)
        {
            return Eligible(settings, parsed, RoleMenuRepairPanelIssue.ChannelMissing);
        }

        if (lookup.Status == RoleMenuPanelLookupStatus.MessageMissing)
        {
            return Eligible(settings, parsed, RoleMenuRepairPanelIssue.MessageMissing);
        }

        if (lookup.Status == RoleMenuPanelLookupStatus.UnexpectedChannelType)
        {
            return Unexpected(
                settings,
                parsed,
                RoleMenuRepairPanelIssue.UnexpectedChannelType);
        }

        if (lookup.Status != RoleMenuPanelLookupStatus.Found || lookup.Panel is null)
        {
            return Unexpected(settings, parsed, RoleMenuRepairPanelIssue.InvalidLookup);
        }

        var panel = lookup.Panel;
        if (panel.GuildId != guildId)
        {
            return Unexpected(settings, parsed, RoleMenuRepairPanelIssue.GuildMismatch);
        }

        if (panel.ChannelId != parsed.ChannelId)
        {
            return Unexpected(settings, parsed, RoleMenuRepairPanelIssue.ChannelMismatch);
        }

        if (panel.MessageId != parsed.MessageId)
        {
            return Unexpected(settings, parsed, RoleMenuRepairPanelIssue.MessageMismatch);
        }

        if (panel.AuthorId != botUserId)
        {
            return Unexpected(settings, parsed, RoleMenuRepairPanelIssue.UnexpectedAuthor);
        }

        if (!panel.HasManageButton)
        {
            return Unexpected(settings, parsed, RoleMenuRepairPanelIssue.MissingManageButton);
        }

        return new RoleMenuRepairInspectionResult(
            RoleMenuRepairInspectionStatus.Healthy,
            settings,
            parsed);
    }

    private static RoleMenuRepairInspectionResult Eligible(
        RoleMenuSettings settings,
        ParsedRoleMenuSettings parsed,
        RoleMenuRepairPanelIssue issue)
        => new(
            RoleMenuRepairInspectionStatus.Eligible,
            settings,
            parsed,
            issue);

    private static RoleMenuRepairInspectionResult Unexpected(
        RoleMenuSettings settings,
        ParsedRoleMenuSettings parsed,
        RoleMenuRepairPanelIssue issue)
        => new(
            RoleMenuRepairInspectionStatus.UnexpectedPanel,
            settings,
            parsed,
            issue);
}
