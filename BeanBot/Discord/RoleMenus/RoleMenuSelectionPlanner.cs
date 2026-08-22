using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BeanBot.Persistence.Models;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuSelectionIssue
{
    None,
    InvalidConfiguredRole,
    DuplicateConfiguredRole,
    TooManyConfiguredRoles,
    InvalidSelectedRole,
    DuplicateSelectedRole,
    RoleNotAllowed,
    TooManySelections,
    InvalidSelectionMode
}

internal sealed record RoleMenuSelectionPlan(
    IReadOnlyList<ulong> SelectedRoleIds,
    IReadOnlyList<ulong> RolesToAdd,
    IReadOnlyList<ulong> RolesToRemove);

internal sealed record RoleMenuSelectionPlanResult(
    RoleMenuSelectionPlan? Plan,
    RoleMenuSelectionIssue Issue)
{
    [MemberNotNullWhen(true, nameof(Plan))]
    internal bool IsValid => Plan is not null && Issue == RoleMenuSelectionIssue.None;
}

internal static class RoleMenuSelectionPlanner
{
    internal static RoleMenuSelectionPlanResult Create(
        IReadOnlyCollection<ulong> configuredRoleIds,
        IReadOnlyCollection<string> submittedRoleValues,
        IReadOnlyCollection<ulong> currentRoleIds,
        RoleMenuSelectionMode selectionMode)
    {
        ArgumentNullException.ThrowIfNull(configuredRoleIds);
        ArgumentNullException.ThrowIfNull(submittedRoleValues);
        ArgumentNullException.ThrowIfNull(currentRoleIds);

        if (!Enum.IsDefined(selectionMode))
        {
            return Invalid(RoleMenuSelectionIssue.InvalidSelectionMode);
        }

        if (configuredRoleIds.Count is < 1 or > RoleMenuConstants.MaximumRoles)
        {
            return Invalid(RoleMenuSelectionIssue.TooManyConfiguredRoles);
        }

        var configuredSet = new HashSet<ulong>();
        foreach (var roleId in configuredRoleIds)
        {
            if (roleId == 0)
            {
                return Invalid(RoleMenuSelectionIssue.InvalidConfiguredRole);
            }

            if (!configuredSet.Add(roleId))
            {
                return Invalid(RoleMenuSelectionIssue.DuplicateConfiguredRole);
            }
        }

        var selectedIds = new List<ulong>(submittedRoleValues.Count);
        var selectedSet = new HashSet<ulong>();
        foreach (var value in submittedRoleValues)
        {
            if (!ulong.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var selectedRoleId)
                || selectedRoleId == 0)
            {
                return Invalid(RoleMenuSelectionIssue.InvalidSelectedRole);
            }

            if (!selectedSet.Add(selectedRoleId))
            {
                return Invalid(RoleMenuSelectionIssue.DuplicateSelectedRole);
            }

            if (!configuredSet.Contains(selectedRoleId))
            {
                return Invalid(RoleMenuSelectionIssue.RoleNotAllowed);
            }

            selectedIds.Add(selectedRoleId);
        }

        if (selectionMode == RoleMenuSelectionMode.Single && selectedIds.Count > 1)
        {
            return Invalid(RoleMenuSelectionIssue.TooManySelections);
        }

        var currentSet = currentRoleIds.ToHashSet();
        var rolesToAdd = selectedIds
            .Where(roleId => !currentSet.Contains(roleId))
            .ToList();
        var rolesToRemove = configuredRoleIds
            .Where(roleId => currentSet.Contains(roleId) && !selectedSet.Contains(roleId))
            .ToList();

        return new RoleMenuSelectionPlanResult(
            new RoleMenuSelectionPlan(selectedIds, rolesToAdd, rolesToRemove),
            RoleMenuSelectionIssue.None);

        static RoleMenuSelectionPlanResult Invalid(RoleMenuSelectionIssue issue)
            => new(null, issue);
    }
}
