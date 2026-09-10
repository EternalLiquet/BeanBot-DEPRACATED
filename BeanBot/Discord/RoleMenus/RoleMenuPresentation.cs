using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuPresentation;

namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuPresentation
{
    internal static string FormatRoleValidationFailure(
        RoleMenuRoleValidationResult validation)
    {
        var issue = validation.Issues[0];
        var roleName = string.IsNullOrWhiteSpace(issue.RoleName)
            ? "A selected role"
            : $"The role **{issue.RoleName}**";
        return issue.Kind switch
        {
            RoleMenuRoleIssueKind.BotMissingManageRoles =>
                "Bean Bot needs the **Manage Roles** permission before it can publish this menu.",
            RoleMenuRoleIssueKind.AdministratorMissingManageRoles =>
                "You no longer have the **Manage Roles** permission required to publish this menu.",
            RoleMenuRoleIssueKind.Duplicate =>
                $"{roleName} was selected more than once. Reopen the setup modal.",
            RoleMenuRoleIssueKind.Missing =>
                "A selected role was deleted or does not belong to this server.",
            RoleMenuRoleIssueKind.Everyone =>
                "The `@everyone` role cannot be self-assigned.",
            RoleMenuRoleIssueKind.Managed =>
                $"{roleName} is managed by Discord or an integration and cannot be assigned.",
            RoleMenuRoleIssueKind.BotHierarchy =>
                $"{roleName} is at or above Bean Bot's highest role. Move Bean Bot above it first.",
            RoleMenuRoleIssueKind.AdministratorHierarchy =>
                $"{roleName} is at or above your highest role and cannot be configured by you.",
            _ => "One or more selected roles cannot be assigned safely."
        };
    }

    internal static string CreateMessageUrl(
        ulong guildId,
        ulong channelId,
        ulong messageId)
        => "https://discord.com/channels/" +
           guildId.ToString(CultureInfo.InvariantCulture) + "/" +
           channelId.ToString(CultureInfo.InvariantCulture) + "/" +
           messageId.ToString(CultureInfo.InvariantCulture);
    internal static string FormatReconciliation(
        RoleMenuSelectionReconciliation reconciliation,
        IReadOnlyDictionary<ulong, string> roleNames)
    {
        var lines = new List<string>();
        AddRoleList(lines, "Added", reconciliation.AddedRoleIds, roleNames);
        AddRoleList(lines, "Removed", reconciliation.RemovedRoleIds, roleNames);
        AddRoleList(
            lines,
            "Still missing",
            reconciliation.MissingSelectedRoleIds,
            roleNames);
        AddRoleList(
            lines,
            "Still assigned",
            reconciliation.StillAssignedUnselectedRoleIds,
            roleNames);
        if (lines.Count == 0)
        {
            lines.Add("Discord's current role state already matches your selection.");
        }

        lines.Add(reconciliation.IsComplete
            ? "Bean Bot rechecked Discord's current role state. No roles outside this menu were changed."
            : "Bean Bot rechecked Discord's current role state, but some requested changes are still " +
              "not applied. No roles outside this menu were changed.");
        return BoundResponseContent(string.Join('\n', lines));
    }

    internal static void AddRoleList(
        List<string> lines,
        string label,
        IReadOnlyCollection<ulong> roleIds,
        IReadOnlyDictionary<ulong, string> roleNames)
    {
        if (roleIds.Count == 0)
        {
            return;
        }

        var names = roleIds.Select(roleId => GetRoleName(roleNames, roleId));
        lines.Add($"**{label} ({roleIds.Count}):** {string.Join(", ", names)}");
    }

    internal static string BoundResponseContent(string content)
    {
        if (content.Length <= RoleMenuConstants.MaximumResponseContentLength)
        {
            return content;
        }

        const string suffix =
            "…\nSome role details were omitted. Open the menu again to verify the current state.";
        var cutoff = RoleMenuConstants.MaximumResponseContentLength - suffix.Length;
        if (cutoff > 0 && char.IsHighSurrogate(content[cutoff - 1]))
        {
            cutoff--;
        }

        return content[..cutoff] + suffix;
    }

    internal static string GetRoleName(
        IReadOnlyDictionary<ulong, string> roleNames,
        ulong roleId)
    {
        var name = roleNames.TryGetValue(roleId, out var resolvedName)
            ? resolvedName
            : "unknown role";
        var normalized = string.Join(' ', name.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        normalized = RoleMenuText.TruncateWithEllipsis(normalized, 40);
        return normalized.Replace("`", "ʼ", StringComparison.Ordinal);
    }

    internal static string FormatConfigurationIssue(RoleMenuMemberWorkflowResult result)
        => result.ConfigurationIssue switch
        {
            RoleMenuMemberConfigurationIssue.SettingsInvalid =>
                $"{result.ConfigurationIssue}: {result.SettingsIssue}",
            RoleMenuMemberConfigurationIssue.PanelInvalid =>
                $"{result.ConfigurationIssue}: {result.PanelIssue}",
            RoleMenuMemberConfigurationIssue.RolesInvalid when result.RoleIssues is { Count: > 0 } =>
                $"{result.ConfigurationIssue}: {result.RoleIssues[0].Kind}",
            _ => result.ConfigurationIssue.ToString()
        };

    internal static string FormatDeletion(RoleMenuDeletionResult result)
        => result switch
        {
            { AuthorizationDenied: true } =>
                "You no longer have the **Manage Roles** permission required to delete role menus.",
            { PanelStatus: RoleMenuPanelDeletionStatus.Failed } =>
                "The published panel is still present, so its saved configuration was kept. Fix " +
                "the channel permissions and retry.",
            { PanelStatus: RoleMenuPanelDeletionStatus.OutcomeUnknown } =>
                "Bean Bot couldn't confirm whether the published panel was deleted, so its saved " +
                "configuration was kept. Retry this command to finish cleanup safely.",
            {
                PanelStatus: RoleMenuPanelDeletionStatus.UnexpectedMessage,
                ConfigurationStatus: RoleMenuConfigurationDeletionStatus.Kept
            } =>
                "The referenced message no longer looked like Bean Bot's panel and was left " +
                "untouched, but the saved configuration could not be deleted. Retry to finish cleanup.",
            {
                PanelStatus: RoleMenuPanelDeletionStatus.UnexpectedMessage,
                ConfigurationStatus: RoleMenuConfigurationDeletionStatus.OutcomeUnknown
            } =>
                "The referenced message no longer looked like Bean Bot's panel and was left " +
                "untouched. Bean Bot couldn't confirm whether the saved configuration was deleted; " +
                "run this command again to check.",
            { PanelStatus: RoleMenuPanelDeletionStatus.UnexpectedMessage } =>
                "The saved configuration was deleted, but the referenced message no longer looked like " +
                "Bean Bot's panel and was left untouched.",
            { ConfigurationStatus: RoleMenuConfigurationDeletionStatus.Kept } =>
                "The published panel is gone, but Bean Bot couldn't delete the saved configuration. " +
                "Retry this command to finish cleanup.",
            { ConfigurationStatus: RoleMenuConfigurationDeletionStatus.OutcomeUnknown } =>
                "The published panel is gone, but Bean Bot couldn't confirm whether its saved " +
                "configuration was deleted. Run this command again to check.",
            { ConfigurationStatus: RoleMenuConfigurationDeletionStatus.AlreadyMissing } =>
                "That role menu was already deleted.",
            _ => "Role menu and saved configuration deleted."
        };

    internal static string FormatTerminalPublication(RoleMenuPublicationStatus status)
        => status switch
        {
            RoleMenuPublicationStatus.PanelOutcomeUnknown =>
                "Discord reported an error while publishing, and Bean Bot could not confirm " +
                "whether a panel was created. Automatic retry was disabled to prevent a duplicate. " +
                "Check the target channel and remove any orphaned panel before running " +
                "`/role-menu create` again.",
            RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed =>
                "Bean Bot confirmed the settings were not saved but could not remove the panel. " +
                "Automatic retry was disabled to prevent a duplicate. Delete that orphaned panel " +
                "manually before running `/role-menu create` again.",
            _ =>
                "Bean Bot could not confirm whether MongoDB saved this panel. The public panel was " +
                "left in place to avoid deleting a possibly committed menu, and automatic retry " +
                "was disabled to prevent a duplicate. Inspect the target channel before running " +
                "`/role-menu create` again."
        };
}
