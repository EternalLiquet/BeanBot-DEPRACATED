using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuSelectionPlannerTests
{
    [Fact]
    public void Create_MultipleSelection_ChangesOnlyConfiguredRoles()
    {
        var result = RoleMenuSelectionPlanner.Create(
            [1UL, 2UL, 3UL],
            ["2", "3"],
            [1UL, 2UL, 99UL],
            RoleMenuSelectionMode.Multiple);

        Assert.True(result.IsValid);
        var plan = Assert.IsType<RoleMenuSelectionPlan>(result.Plan);
        Assert.Equal([3UL], plan.RolesToAdd);
        Assert.Equal([1UL], plan.RolesToRemove);
        Assert.DoesNotContain(99UL, plan.RolesToRemove);
    }

    [Fact]
    public void Create_EmptySelection_ClearsOnlyConfiguredRoles()
    {
        var result = RoleMenuSelectionPlanner.Create(
            [1UL, 2UL],
            [],
            [1UL, 2UL, 99UL],
            RoleMenuSelectionMode.Multiple);

        Assert.True(result.IsValid);
        var plan = Assert.IsType<RoleMenuSelectionPlan>(result.Plan);
        Assert.Empty(plan.RolesToAdd);
        Assert.Equal([1UL, 2UL], plan.RolesToRemove);
    }

    [Fact]
    public void Create_SingleSelection_ReplacesExistingMenuRole()
    {
        var result = RoleMenuSelectionPlanner.Create(
            [1UL, 2UL],
            ["2"],
            [1UL],
            RoleMenuSelectionMode.Exclusive);

        Assert.True(result.IsValid);
        var plan = Assert.IsType<RoleMenuSelectionPlan>(result.Plan);
        Assert.Equal([2UL], plan.RolesToAdd);
        Assert.Equal([1UL], plan.RolesToRemove);
    }

    [Theory]
    [InlineData("not-a-role", (int)RoleMenuSelectionIssue.InvalidSelectedRole)]
    [InlineData("0", (int)RoleMenuSelectionIssue.InvalidSelectedRole)]
    [InlineData("999", (int)RoleMenuSelectionIssue.RoleNotAllowed)]
    public void Create_TamperedValue_IsRejected(
        string submittedValue,
        int expectedIssue)
    {
        var result = RoleMenuSelectionPlanner.Create(
            [1UL, 2UL],
            [submittedValue],
            [],
            RoleMenuSelectionMode.Multiple);

        Assert.False(result.IsValid);
        Assert.Equal((RoleMenuSelectionIssue)expectedIssue, result.Issue);
    }

    [Fact]
    public void Create_DuplicateSubmittedValue_IsRejected()
    {
        var result = RoleMenuSelectionPlanner.Create(
            [1UL, 2UL],
            ["1", "1"],
            [],
            RoleMenuSelectionMode.Multiple);

        Assert.Equal(RoleMenuSelectionIssue.DuplicateSelectedRole, result.Issue);
    }

    [Fact]
    public void Create_TooManySingleSelections_IsRejected()
    {
        var result = RoleMenuSelectionPlanner.Create(
            [1UL, 2UL],
            ["1", "2"],
            [],
            RoleMenuSelectionMode.Exclusive);

        Assert.Equal(RoleMenuSelectionIssue.TooManySelections, result.Issue);
    }

    [Fact]
    public void Create_InvalidPersistedMode_IsRejected()
    {
        var result = RoleMenuSelectionPlanner.Create(
            [1UL],
            ["1"],
            [],
            (RoleMenuSelectionMode)999);

        Assert.Equal(RoleMenuSelectionIssue.InvalidSelectionMode, result.Issue);
    }

    [Fact]
    public void Create_DuplicateConfiguredRole_IsRejected()
    {
        var result = RoleMenuSelectionPlanner.Create(
            [1UL, 1UL],
            ["1"],
            [],
            RoleMenuSelectionMode.Multiple);

        Assert.Equal(RoleMenuSelectionIssue.DuplicateConfiguredRole, result.Issue);
    }

    [Fact]
    public void Create_EmptyConfiguredRoleSet_IsRejected()
    {
        var result = RoleMenuSelectionPlanner.Create(
            [],
            [],
            [],
            RoleMenuSelectionMode.Multiple);

        Assert.Equal(RoleMenuSelectionIssue.TooManyConfiguredRoles, result.Issue);
    }

    [Fact]
    public void Create_ZeroConfiguredRole_IsRejected()
    {
        var result = RoleMenuSelectionPlanner.Create(
            [0UL],
            [],
            [],
            RoleMenuSelectionMode.Multiple);

        Assert.Equal(RoleMenuSelectionIssue.InvalidConfiguredRole, result.Issue);
    }
}
