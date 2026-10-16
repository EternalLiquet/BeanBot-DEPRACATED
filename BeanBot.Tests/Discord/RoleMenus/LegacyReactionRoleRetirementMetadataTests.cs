using System.Reflection;
using BeanBot.Discord.RoleMenus;
using Discord;
using Discord.Interactions;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class LegacyReactionRoleRetirementMetadataTests
{
    [Fact]
    public void RetirementCommand_IsPartOfExistingManageRolesAdminModule()
    {
        var method = typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.RetireLegacyAsync));
        var permission = typeof(RoleMenuAdminModule).GetCustomAttribute<RequireUserPermissionAttribute>();

        Assert.NotNull(method);
        var command = method.GetCustomAttribute<SlashCommandAttribute>();
        Assert.NotNull(command);
        Assert.Equal("retire-legacy", command.Name);
        Assert.Equal(RunMode.Sync, command.RunMode);
        Assert.NotNull(permission);
        Assert.Equal(GuildPermission.ManageRoles, permission.GuildPermission);
    }

    [Fact]
    public void RetirementConfirmation_UsesSynchronousInteractionRunMode()
    {
        var method = typeof(RoleMenuAdminModule).GetMethod(
            nameof(RoleMenuAdminModule.ConfirmLegacyRetirementAsync));

        Assert.NotNull(method);
        var component = method.GetCustomAttribute<ComponentInteractionAttribute>();
        Assert.NotNull(component);
        Assert.Equal(RunMode.Sync, component.RunMode);
    }
}
