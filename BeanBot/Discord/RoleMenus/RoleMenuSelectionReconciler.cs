namespace BeanBot.Discord.RoleMenus;

internal sealed record RoleMenuSelectionReconciliation(
    IReadOnlyList<ulong> AddedRoleIds,
    IReadOnlyList<ulong> RemovedRoleIds,
    IReadOnlyList<ulong> MissingSelectedRoleIds,
    IReadOnlyList<ulong> StillAssignedUnselectedRoleIds)
{
    internal bool IsComplete => MissingSelectedRoleIds.Count == 0
                                && StillAssignedUnselectedRoleIds.Count == 0;
}

internal static class RoleMenuSelectionReconciler
{
    internal static RoleMenuSelectionReconciliation Create(
        IReadOnlyCollection<ulong> configuredRoleIds,
        IReadOnlyCollection<ulong> selectedRoleIds,
        IReadOnlyCollection<ulong> beforeRoleIds,
        IReadOnlyCollection<ulong> afterRoleIds)
    {
        ArgumentNullException.ThrowIfNull(configuredRoleIds);
        ArgumentNullException.ThrowIfNull(selectedRoleIds);
        ArgumentNullException.ThrowIfNull(beforeRoleIds);
        ArgumentNullException.ThrowIfNull(afterRoleIds);

        var configured = configuredRoleIds.ToHashSet();
        var selected = selectedRoleIds.ToHashSet();
        var before = beforeRoleIds.Where(configured.Contains).ToHashSet();
        var after = afterRoleIds.Where(configured.Contains).ToHashSet();
        return new RoleMenuSelectionReconciliation(
            configuredRoleIds.Where(roleId => after.Contains(roleId) && !before.Contains(roleId))
                .ToList(),
            configuredRoleIds.Where(roleId => before.Contains(roleId) && !after.Contains(roleId))
                .ToList(),
            selectedRoleIds.Where(roleId => !after.Contains(roleId)).ToList(),
            configuredRoleIds.Where(roleId => !selected.Contains(roleId) && after.Contains(roleId))
                .ToList());
    }
}
