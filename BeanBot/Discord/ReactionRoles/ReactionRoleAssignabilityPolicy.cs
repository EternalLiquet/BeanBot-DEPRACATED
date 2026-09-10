namespace BeanBot.Discord.ReactionRoles;

internal enum ReactionRoleAssignabilityStatus
{
    Allowed,
    RoleMissing,
    EveryoneRole,
    ManagedRole,
    BotMissingManageRoles,
    BotHierarchyTooLow,
    InvokerHierarchyTooLow
}

internal readonly record struct ReactionRoleAssignabilityFacts(
    bool RoleExists,
    bool IsEveryoneRole,
    bool IsManagedRole,
    bool BotCanManageRoles,
    int TargetRolePosition,
    int BotHierarchy);

internal static class ReactionRoleAssignabilityPolicy
{
    internal static ReactionRoleAssignabilityStatus EvaluateForSetup(
        ReactionRoleAssignabilityFacts facts,
        bool invokerIsGuildOwner,
        int invokerHierarchy)
    {
        var botStatus = EvaluateForRuntime(facts);
        if (botStatus != ReactionRoleAssignabilityStatus.Allowed)
        {
            return botStatus;
        }

        return !invokerIsGuildOwner && facts.TargetRolePosition >= invokerHierarchy
            ? ReactionRoleAssignabilityStatus.InvokerHierarchyTooLow
            : ReactionRoleAssignabilityStatus.Allowed;
    }

    internal static ReactionRoleAssignabilityStatus EvaluateForRuntime(
        ReactionRoleAssignabilityFacts facts)
    {
        if (!facts.RoleExists)
        {
            return ReactionRoleAssignabilityStatus.RoleMissing;
        }

        if (facts.IsEveryoneRole)
        {
            return ReactionRoleAssignabilityStatus.EveryoneRole;
        }

        if (facts.IsManagedRole)
        {
            return ReactionRoleAssignabilityStatus.ManagedRole;
        }

        if (!facts.BotCanManageRoles)
        {
            return ReactionRoleAssignabilityStatus.BotMissingManageRoles;
        }

        return facts.TargetRolePosition >= facts.BotHierarchy
            ? ReactionRoleAssignabilityStatus.BotHierarchyTooLow
            : ReactionRoleAssignabilityStatus.Allowed;
    }
}
