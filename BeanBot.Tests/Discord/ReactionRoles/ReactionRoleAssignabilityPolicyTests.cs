using BeanBot.Discord.ReactionRoles;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class ReactionRoleAssignabilityPolicyTests
{
    [Fact]
    public void EvaluateForSetup_RoleBelowInvokerAndBot_IsAllowed()
    {
        var status = ReactionRoleAssignabilityPolicy.EvaluateForSetup(
            CreateFacts(targetPosition: 3, botHierarchy: 10),
            invokerIsGuildOwner: false,
            invokerHierarchy: 8);

        Assert.Equal(ReactionRoleAssignabilityStatus.Allowed, status);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void EvaluateForSetup_RoleAtOrAboveInvokerHierarchy_IsRejected(int targetPosition)
    {
        var status = ReactionRoleAssignabilityPolicy.EvaluateForSetup(
            CreateFacts(targetPosition, botHierarchy: 10),
            invokerIsGuildOwner: false,
            invokerHierarchy: 8);

        Assert.Equal(ReactionRoleAssignabilityStatus.InvokerHierarchyTooLow, status);
    }

    [Fact]
    public void EvaluateForSetup_GuildOwner_BypassesInvokerHierarchyOnly()
    {
        var status = ReactionRoleAssignabilityPolicy.EvaluateForSetup(
            CreateFacts(targetPosition: 9, botHierarchy: 10),
            invokerIsGuildOwner: true,
            invokerHierarchy: 1);

        Assert.Equal(ReactionRoleAssignabilityStatus.Allowed, status);
    }

    [Fact]
    public void EvaluateForSetup_EveryoneRole_IsRejected()
    {
        var status = ReactionRoleAssignabilityPolicy.EvaluateForSetup(
            CreateFacts(isEveryoneRole: true),
            invokerIsGuildOwner: true,
            invokerHierarchy: 10);

        Assert.Equal(ReactionRoleAssignabilityStatus.EveryoneRole, status);
    }

    [Fact]
    public void EvaluateForSetup_ManagedRole_IsRejected()
    {
        var status = ReactionRoleAssignabilityPolicy.EvaluateForSetup(
            CreateFacts(isManagedRole: true),
            invokerIsGuildOwner: true,
            invokerHierarchy: 10);

        Assert.Equal(ReactionRoleAssignabilityStatus.ManagedRole, status);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void EvaluateForSetup_RoleAtOrAboveBotHierarchy_IsRejected(int targetPosition)
    {
        var status = ReactionRoleAssignabilityPolicy.EvaluateForSetup(
            CreateFacts(targetPosition, botHierarchy: 10),
            invokerIsGuildOwner: true,
            invokerHierarchy: 20);

        Assert.Equal(ReactionRoleAssignabilityStatus.BotHierarchyTooLow, status);
    }

    [Fact]
    public void EvaluateForSetup_MissingBotManageRoles_IsRejected()
    {
        var status = ReactionRoleAssignabilityPolicy.EvaluateForSetup(
            CreateFacts(botCanManageRoles: false),
            invokerIsGuildOwner: true,
            invokerHierarchy: 10);

        Assert.Equal(ReactionRoleAssignabilityStatus.BotMissingManageRoles, status);
    }

    [Fact]
    public void EvaluateForRuntime_MissingPersistedRole_IsRejected()
    {
        var status = ReactionRoleAssignabilityPolicy.EvaluateForRuntime(
            CreateFacts(roleExists: false));

        Assert.Equal(ReactionRoleAssignabilityStatus.RoleMissing, status);
    }

    [Fact]
    public void EvaluateForRuntime_DoesNotApplyHistoricalInvokerHierarchy()
    {
        var status = ReactionRoleAssignabilityPolicy.EvaluateForRuntime(
            CreateFacts(targetPosition: 9, botHierarchy: 10));

        Assert.Equal(ReactionRoleAssignabilityStatus.Allowed, status);
    }

    private static ReactionRoleAssignabilityFacts CreateFacts(
        int targetPosition = 3,
        int botHierarchy = 10,
        bool roleExists = true,
        bool isEveryoneRole = false,
        bool isManagedRole = false,
        bool botCanManageRoles = true)
        => new(
            roleExists,
            isEveryoneRole,
            isManagedRole,
            botCanManageRoles,
            targetPosition,
            botHierarchy);
}
