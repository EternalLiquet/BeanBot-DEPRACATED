using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuMigrationIdentityTests
{
    [Fact]
    public void CreateMenuId_IsDeterministicForLegacySource()
    {
        var first = RoleMenuMigrationIdentity.CreateMenuId(123UL, 456UL);
        var second = RoleMenuMigrationIdentity.CreateMenuId(123UL, 456UL);

        Assert.NotEqual(default, first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, RoleMenuMigrationIdentity.CreateMenuId(124UL, 456UL));
        Assert.NotEqual(first, RoleMenuMigrationIdentity.CreateMenuId(123UL, 457UL));
    }

    [Fact]
    public void CreateMenuId_RejectsMissingSourceIdentity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RoleMenuMigrationIdentity.CreateMenuId(0UL, 456UL));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RoleMenuMigrationIdentity.CreateMenuId(123UL, 0UL));
    }

    [Fact]
    public void BuildMessageLink_UsesExactDiscordIdentity()
    {
        Assert.Equal(
            "https://discord.com/channels/1/2/3",
            RoleMenuMigrationIdentity.BuildMessageLink(1UL, 2UL, 3UL));
    }
}
