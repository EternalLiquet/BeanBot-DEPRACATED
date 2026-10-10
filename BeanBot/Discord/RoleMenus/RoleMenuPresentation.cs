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
        var role = string.IsNullOrWhiteSpace(issue.RoleName)
            ? "one of those roles"
            : $"**{issue.RoleName}**";
        return issue.Kind switch
        {
            RoleMenuRoleIssueKind.BotMissingManageRoles =>
                "I need the **Manage Roles** permission to publish this menu.",
            RoleMenuRoleIssueKind.AdministratorMissingManageRoles =>
                "You no longer have the **Manage Roles** permission, so you can't publish this menu.",
            RoleMenuRoleIssueKind.Duplicate =>
                $"You picked {role} more than once. Run `/role-menu create` again and pick each role once.",
            RoleMenuRoleIssueKind.Missing =>
                "One of the roles you picked was deleted or isn't from this server. " +
                "Run `/role-menu create` again.",
            RoleMenuRoleIssueKind.Everyone =>
                "`@everyone` can't be added to a role menu.",
            RoleMenuRoleIssueKind.Managed =>
                $"I can't assign {role} because Discord or an integration manages it.",
            RoleMenuRoleIssueKind.BotHierarchy =>
                $"I can't assign {role} because it's at or above my highest role. " +
                "Move my role above it, then try again.",
            RoleMenuRoleIssueKind.AdministratorHierarchy =>
                $"You can't add {role} because it's at or above your highest role.",
            _ => "I can't assign one of the roles you picked."
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
        var sentences = new List<string>();
        AddRoleSentence(sentences, "Added", reconciliation.AddedRoleIds, roleNames);
        AddRoleSentence(sentences, "Removed", reconciliation.RemovedRoleIds, roleNames);
        AddRoleSentence(
            sentences,
            "I couldn't add",
            reconciliation.MissingSelectedRoleIds,
            roleNames);
        AddRoleSentence(
            sentences,
            "I couldn't remove",
            reconciliation.StillAssignedUnselectedRoleIds,
            roleNames);
        if (sentences.Count == 0)
        {
            sentences.Add(reconciliation.UnchangedSelectedRoleIds.Count == 0
                ? "You don't have any roles from this menu."
                : $"You already have {JoinRoleNames(reconciliation.UnchangedSelectedRoleIds, roleNames)}.");
        }

        if (!reconciliation.IsComplete)
        {
            sentences.Add("Open the menu again to check your roles before trying again.");
        }

        return BoundResponseContent(string.Join(' ', sentences));
    }

    internal static void AddRoleSentence(
        List<string> sentences,
        string lead,
        IReadOnlyCollection<ulong> roleIds,
        IReadOnlyDictionary<ulong, string> roleNames)
    {
        if (roleIds.Count == 0)
        {
            return;
        }

        sentences.Add($"{lead} {JoinRoleNames(roleIds, roleNames)}.");
    }

    internal static string JoinRoleNames(
        IReadOnlyCollection<ulong> roleIds,
        IReadOnlyDictionary<ulong, string> roleNames)
    {
        var names = roleIds.Select(roleId => GetRoleName(roleNames, roleId)).ToList();
        return names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[^1]}"
        };
    }

    internal static string BoundResponseContent(string content)
    {
        if (content.Length <= RoleMenuConstants.MaximumResponseContentLength)
        {
            return content;
        }

        const string suffix =
            "…\nThat list was too long to show in full. Open the menu again to see your roles.";
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
            : "an unknown role";
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

    internal static string FormatConfirmedDeletion(RoleMenuConfirmedDeletion deletion)
        => deletion switch
        {
            { Status: RoleMenuConfirmedDeletionStatus.Attempted, Result: { } result } =>
                FormatDeletion(result),
            { Status: RoleMenuConfirmedDeletionStatus.Changed } =>
                "That role menu changed. Run `/role-menu delete` to see it again.",
            _ => "That role menu was already deleted."
        };

    internal static string FormatDeletion(RoleMenuDeletionResult result)
        => result switch
        {
            { AuthorizationDenied: true } =>
                "You no longer have the **Manage Roles** permission, so you can't delete role menus.",
            { PanelStatus: RoleMenuPanelDeletionStatus.Failed } =>
                "I couldn't delete the menu message, so the menu is still set up. Check my " +
                "permissions in that channel, then run `/role-menu delete` again.",
            { PanelStatus: RoleMenuPanelDeletionStatus.OutcomeUnknown } =>
                "I couldn't tell whether the menu message was deleted, so I kept the menu's " +
                "saved settings. Run `/role-menu delete` again to finish.",
            {
                PanelStatus: RoleMenuPanelDeletionStatus.UnexpectedMessage,
                ConfigurationStatus: RoleMenuConfigurationDeletionStatus.Kept
            } =>
                "The message this menu points to doesn't look like a role menu anymore, so I left " +
                "it alone. I also couldn't remove the menu's saved settings. Run `/role-menu delete` " +
                "again to finish.",
            {
                PanelStatus: RoleMenuPanelDeletionStatus.UnexpectedMessage,
                ConfigurationStatus: RoleMenuConfigurationDeletionStatus.OutcomeUnknown
            } =>
                "The message this menu points to doesn't look like a role menu anymore, so I left " +
                "it alone. I couldn't tell whether the menu's saved settings were removed. Run " +
                "`/role-menu delete` again to check.",
            { PanelStatus: RoleMenuPanelDeletionStatus.UnexpectedMessage } =>
                "I removed the menu's saved settings. The message it pointed to doesn't look like a " +
                "role menu anymore, so I left it alone.",
            { ConfigurationStatus: RoleMenuConfigurationDeletionStatus.Kept } =>
                "The menu message is gone, but I couldn't remove its saved settings. Run " +
                "`/role-menu delete` again to finish.",
            { ConfigurationStatus: RoleMenuConfigurationDeletionStatus.OutcomeUnknown } =>
                "The menu message is gone, but I couldn't tell whether its saved settings were " +
                "removed. Run `/role-menu delete` again to check.",
            { ConfigurationStatus: RoleMenuConfigurationDeletionStatus.AlreadyMissing } =>
                "That role menu was already deleted.",
            _ => "Role menu deleted."
        };

    internal static string FormatTerminalPublication(RoleMenuPublicationStatus status)
        => status switch
        {
            RoleMenuPublicationStatus.PanelOutcomeUnknown =>
                "Discord returned an error while I was posting your menu, and I couldn't tell " +
                "whether it went through. Check the channel before you run `/role-menu create` " +
                "again. If the menu is there, delete that message first so you don't end up with two.",
            RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed =>
                "I posted your menu but couldn't save it, and I couldn't take the message back down. " +
                "Delete that menu message yourself before you run `/role-menu create` again.",
            _ =>
                "I posted your menu, but I couldn't confirm it was saved, so I left it in place. " +
                "Check the channel before you run `/role-menu create` again so you don't end up " +
                "with two menus."
        };
}
