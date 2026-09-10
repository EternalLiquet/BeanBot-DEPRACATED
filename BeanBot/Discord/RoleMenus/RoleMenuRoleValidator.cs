namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuRoleIssueKind
{
    BotMissingManageRoles,
    AdministratorMissingManageRoles,
    Duplicate,
    Missing,
    Everyone,
    Managed,
    BotHierarchy,
    AdministratorHierarchy
}

internal readonly record struct RoleMenuRoleSnapshot(
    ulong Id,
    string Name,
    bool IsEveryone,
    bool IsManaged,
    int Position);

internal readonly record struct RoleMenuActorSnapshot(
    bool CanManageRoles,
    int Hierarchy,
    bool IsGuildOwner);

internal readonly record struct RoleMenuRoleIssue(
    ulong? RoleId,
    string? RoleName,
    RoleMenuRoleIssueKind Kind);

internal sealed record RoleMenuRoleValidationResult(
    IReadOnlyList<RoleMenuRoleSnapshot> Roles,
    IReadOnlyList<RoleMenuRoleIssue> Issues)
{
    internal bool IsValid => Issues.Count == 0;
}

internal static class RoleMenuRoleValidator
{
    internal static RoleMenuRoleValidationResult Validate(
        IReadOnlyCollection<ulong> requestedRoleIds,
        IReadOnlyCollection<RoleMenuRoleSnapshot> availableRoles,
        RoleMenuActorSnapshot bot,
        RoleMenuActorSnapshot? administrator = null)
    {
        ArgumentNullException.ThrowIfNull(requestedRoleIds);
        ArgumentNullException.ThrowIfNull(availableRoles);

        var issues = new List<RoleMenuRoleIssue>();
        if (!bot.CanManageRoles)
        {
            issues.Add(new RoleMenuRoleIssue(
                null,
                null,
                RoleMenuRoleIssueKind.BotMissingManageRoles));
        }

        if (administrator is { CanManageRoles: false })
        {
            issues.Add(new RoleMenuRoleIssue(
                null,
                null,
                RoleMenuRoleIssueKind.AdministratorMissingManageRoles));
        }

        var availableById = availableRoles
            .GroupBy(role => role.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var resolved = new List<RoleMenuRoleSnapshot>(requestedRoleIds.Count);
        var seen = new HashSet<ulong>();
        foreach (var roleId in requestedRoleIds)
        {
            if (!seen.Add(roleId))
            {
                var duplicateRoleName = availableById.TryGetValue(roleId, out var duplicateRole)
                    ? duplicateRole.Name
                    : null;
                issues.Add(new RoleMenuRoleIssue(
                    roleId,
                    duplicateRoleName,
                    RoleMenuRoleIssueKind.Duplicate));
                continue;
            }

            if (!availableById.TryGetValue(roleId, out var role))
            {
                issues.Add(new RoleMenuRoleIssue(
                    roleId,
                    null,
                    RoleMenuRoleIssueKind.Missing));
                continue;
            }

            resolved.Add(role);
            if (role.IsEveryone)
            {
                issues.Add(new RoleMenuRoleIssue(
                    role.Id,
                    role.Name,
                    RoleMenuRoleIssueKind.Everyone));
            }
            else if (role.IsManaged)
            {
                issues.Add(new RoleMenuRoleIssue(
                    role.Id,
                    role.Name,
                    RoleMenuRoleIssueKind.Managed));
            }
            else if (role.Position >= bot.Hierarchy)
            {
                issues.Add(new RoleMenuRoleIssue(
                    role.Id,
                    role.Name,
                    RoleMenuRoleIssueKind.BotHierarchy));
            }
            else if (administrator is { IsGuildOwner: false } admin
                     && role.Position >= admin.Hierarchy)
            {
                issues.Add(new RoleMenuRoleIssue(
                    role.Id,
                    role.Name,
                    RoleMenuRoleIssueKind.AdministratorHierarchy));
            }
        }

        return new RoleMenuRoleValidationResult(resolved, issues);
    }
}
