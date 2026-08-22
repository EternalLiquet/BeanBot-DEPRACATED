using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuRoleValidatorTests
{
    private static readonly RoleMenuActorSnapshot ValidBot = new(true, 50, false);
    private static readonly RoleMenuActorSnapshot ValidAdministrator = new(true, 40, false);

    [Fact]
    public void Validate_AcceptsAssignableRolesBelowBothActors()
    {
        var roles = new[]
        {
            Role(1, "Games", position: 10),
            Role(2, "Regions", position: 20)
        };

        var result = RoleMenuRoleValidator.Validate(
            [1UL, 2UL],
            roles,
            ValidBot,
            ValidAdministrator);

        Assert.True(result.IsValid);
        Assert.Equal([1UL, 2UL], result.Roles.Select(role => role.Id));
    }

    [Fact]
    public void Validate_RejectsMissingManageRolesPermission()
    {
        var result = RoleMenuRoleValidator.Validate(
            [1UL],
            [Role(1, "Games", position: 10)],
            ValidBot with { CanManageRoles = false },
            ValidAdministrator);

        Assert.Contains(
            result.Issues,
            issue => issue.Kind == RoleMenuRoleIssueKind.BotMissingManageRoles);
    }

    [Fact]
    public void Validate_RejectsAdministratorWhoseManageRolesWasRevoked()
    {
        var result = RoleMenuRoleValidator.Validate(
            [1UL],
            [Role(1, "Games", position: 10)],
            ValidBot,
            ValidAdministrator with { CanManageRoles = false });

        Assert.Contains(
            result.Issues,
            issue => issue.Kind == RoleMenuRoleIssueKind.AdministratorMissingManageRoles);
    }

    [Theory]
    [InlineData(true, false, RoleMenuRoleIssueKind.Everyone)]
    [InlineData(false, true, RoleMenuRoleIssueKind.Managed)]
    public void Validate_RejectsReservedRoles(
        bool isEveryone,
        bool isManaged,
        RoleMenuRoleIssueKind expectedIssue)
    {
        var role = new RoleMenuRoleSnapshot(1, "Reserved", isEveryone, isManaged, 1);

        var result = RoleMenuRoleValidator.Validate(
            [1UL],
            [role],
            ValidBot,
            ValidAdministrator);

        Assert.Contains(result.Issues, issue => issue.Kind == expectedIssue);
    }

    [Fact]
    public void Validate_RejectsBotHierarchyEquality()
    {
        var result = RoleMenuRoleValidator.Validate(
            [1UL],
            [Role(1, "Too high", position: ValidBot.Hierarchy)],
            ValidBot,
            ValidAdministrator);

        Assert.Contains(
            result.Issues,
            issue => issue.Kind == RoleMenuRoleIssueKind.BotHierarchy);
    }

    [Fact]
    public void Validate_RejectsAdministratorHierarchyEquality()
    {
        var result = RoleMenuRoleValidator.Validate(
            [1UL],
            [Role(1, "Too high", position: ValidAdministrator.Hierarchy)],
            ValidBot,
            ValidAdministrator);

        Assert.Contains(
            result.Issues,
            issue => issue.Kind == RoleMenuRoleIssueKind.AdministratorHierarchy);
    }

    [Fact]
    public void Validate_GuildOwnerMayConfigureRolesAboveOwnHierarchy()
    {
        var result = RoleMenuRoleValidator.Validate(
            [1UL],
            [Role(1, "Owner choice", position: 45)],
            ValidBot with { Hierarchy = 50 },
            ValidAdministrator with { IsGuildOwner = true });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsDeletedDuplicateAndUnknownRoles()
    {
        var result = RoleMenuRoleValidator.Validate(
            [1UL, 1UL, 999UL],
            [Role(1, "Games", position: 10)],
            ValidBot,
            ValidAdministrator);

        Assert.Contains(result.Issues, issue => issue.Kind == RoleMenuRoleIssueKind.Duplicate);
        Assert.Contains(result.Issues, issue => issue.Kind == RoleMenuRoleIssueKind.Missing);
    }

    private static RoleMenuRoleSnapshot Role(
        ulong id,
        string name,
        bool isEveryone = false,
        bool isManaged = false,
        int position = 1)
        => new(id, name, isEveryone, isManaged, position);
}
