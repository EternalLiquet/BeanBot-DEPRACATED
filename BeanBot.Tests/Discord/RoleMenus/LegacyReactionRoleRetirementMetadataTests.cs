using System.Reflection;
using BeanBot.Discord.ReactionRoles;
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

    [Fact]
    public void AdminModule_HasRetirementAwareConstructorWithoutReplacingExistingDependencies()
    {
        var constructor = typeof(RoleMenuAdminModule).GetConstructors()
            .Single(candidate => candidate.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(ReactionRoleService)));
        var parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToList();

        Assert.Contains(typeof(RoleMenuInteractionService), parameters);
        Assert.Contains(typeof(DiscordRoleMenuClient), parameters);
        Assert.Contains(typeof(RoleMenuAdministrationService), parameters);
        Assert.Contains(typeof(ReactionRoleService), parameters);
    }
}
