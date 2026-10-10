using System.Reflection;
using BeanBot.Discord.RoleMenus;
using Discord;
using Discord.Interactions;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuInteractionMetadataTests
{
    [Fact]
    public void AdminModule_IsGuildOnlyAndRequiresManageRoles()
    {
        var module = typeof(RoleMenuAdminModule);

        var context = module.GetCustomAttribute<RequireContextAttribute>();
        var permission = module.GetCustomAttribute<RequireUserPermissionAttribute>();
        var defaultPermission = module.GetCustomAttribute<DefaultMemberPermissionsAttribute>();
        var registrationContext = module.GetCustomAttribute<CommandContextTypeAttribute>();

        Assert.NotNull(context);
        Assert.Equal(ContextType.Guild, context.Contexts);
        Assert.NotNull(permission);
        Assert.Equal(GuildPermission.ManageRoles, permission.GuildPermission);
        Assert.NotNull(defaultPermission);
        Assert.Equal(GuildPermission.ManageRoles, defaultPermission.Permissions);
        Assert.NotNull(registrationContext);
        Assert.Equal([InteractionContextType.Guild], registrationContext.ContextTypes);
    }

    [Fact]
    public void MigrationCommand_IsPartOfExistingAdminModule()
    {
        var method = typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.MigrateAsync));

        Assert.NotNull(method);
        var slashCommand = method.GetCustomAttribute<SlashCommandAttribute>();
        Assert.NotNull(slashCommand);
        Assert.Equal("migrate", slashCommand.Name);
        Assert.Equal(RunMode.Sync, slashCommand.RunMode);
        Assert.True(method.GetParameters()[0].IsOptional);
        Assert.NotNull(typeof(RoleMenuAdminModule).GetMethod(
            nameof(RoleMenuAdminModule.ChangeMigrationPageAsync))
            ?.GetCustomAttribute<ComponentInteractionAttribute>());
        Assert.NotNull(typeof(RoleMenuAdminModule).GetMethod(
            nameof(RoleMenuAdminModule.SelectMigrationPanelAsync))
            ?.GetCustomAttribute<ComponentInteractionAttribute>());
    }

    [Fact]
    public void AdminCommands_ShareOneGroupedInteractionModule()
    {
        var groupedRoleMenuModules = typeof(RoleMenuAdminModule).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract
                           && typeof(RoleMenuModuleBase).IsAssignableFrom(type)
                           && type.GetCustomAttribute<GroupAttribute>() is not null)
            .ToList();
        var module = Assert.Single(groupedRoleMenuModules);
        Assert.Equal(typeof(RoleMenuAdminModule), module);
        Assert.NotNull(module.GetMethod(nameof(RoleMenuAdminModule.CreateAsync)));
        Assert.NotNull(module.GetMethod(nameof(RoleMenuAdminModule.EditAsync)));
        Assert.NotNull(module.GetMethod(nameof(RoleMenuAdminModule.DeleteAsync)));
    }

    [Fact]
    public void MessageCommandModule_IsGuildOnlyAndRequiresManageRoles()
    {
        var module = typeof(RoleMenuMessageCommandModule);
        Assert.Equal(ContextType.Guild, module.GetCustomAttribute<RequireContextAttribute>()?.Contexts);
        Assert.Equal(GuildPermission.ManageRoles, module.GetCustomAttribute<RequireUserPermissionAttribute>()?.GuildPermission);
        Assert.Equal(GuildPermission.ManageRoles, module.GetCustomAttribute<DefaultMemberPermissionsAttribute>()?.Permissions);
        Assert.Equal([InteractionContextType.Guild], module.GetCustomAttribute<CommandContextTypeAttribute>()?.ContextTypes);
        var command = typeof(RoleMenuMessageCommandModule)
            .GetMethod(nameof(RoleMenuMessageCommandModule.DeleteRoleMenuAsync))
            ?.GetCustomAttribute<MessageCommandAttribute>();
        Assert.NotNull(command);
        Assert.Equal("Delete Role Menu", command.Name);
        Assert.Equal(RunMode.Sync, command.RunMode);
    }

    [Fact]
    public void CreateModal_UsesNativeBoundedRoleAndTextChannelSelectors()
    {
        var roles = Assert.IsAssignableFrom<PropertyInfo>(
            typeof(RoleMenuCreateModal).GetProperty(nameof(RoleMenuCreateModal.Roles)));
        var channel = Assert.IsAssignableFrom<PropertyInfo>(
            typeof(RoleMenuCreateModal).GetProperty(nameof(RoleMenuCreateModal.TargetChannel)));

        var roleSelector = roles.GetCustomAttribute<ModalRoleSelectAttribute>();
        var channelSelector = channel.GetCustomAttribute<ModalChannelSelectAttribute>();
        var channelTypes = channel.GetCustomAttribute<ChannelTypesAttribute>();

        Assert.NotNull(roleSelector);
        Assert.Equal(1, roleSelector.MinValues);
        Assert.Equal(25, roleSelector.MaxValues);
        Assert.NotNull(channelSelector);
        Assert.Equal(1, channelSelector.MinValues);
        Assert.Equal(1, channelSelector.MaxValues);
        Assert.NotNull(channelTypes);
        Assert.Equal([ChannelType.Text], channelTypes.ChannelTypes);
    }

    [Fact]
    public void CreateModal_DefaultsAndTextValuesRoundTrip()
    {
        var modal = new RoleMenuCreateModal
        {
            PanelTitle = "Games",
            Description = "Choose games",
            SelectionMode = "single"
        };

        Assert.Equal("Create a role menu", modal.Title);
        Assert.Equal("Games", modal.PanelTitle);
        Assert.Equal("Choose games", modal.Description);
        Assert.Empty(modal.Roles);
        Assert.Equal("single", modal.SelectionMode);
        Assert.Null(modal.TargetChannel);
    }

    [Fact]
    public void SelectionMode_IsNativeRadioGroupWithSingleAndMultipleChoices()
    {
        var property = Assert.IsAssignableFrom<PropertyInfo>(
            typeof(RoleMenuCreateModal).GetProperty(nameof(RoleMenuCreateModal.SelectionMode)));

        Assert.NotNull(property.GetCustomAttribute<ModalRadioGroupAttribute>());
        var options = property.GetCustomAttributes<ModalRadioGroupOptionAttribute>().ToList();
        Assert.Equal(2, options.Count);
        Assert.Contains(options, option => option.Value == "multiple" && option.IsDefault);
        Assert.Contains(options, option => option.Value == "single" && !option.IsDefault);
    }

    [Fact]
    public void RepairCommand_UsesIdFreePickerAndOptionalTextChannelTarget()
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.RepairAsync)));
        var command = method.GetCustomAttribute<SlashCommandAttribute>();
        var parameters = method.GetParameters();

        Assert.NotNull(command);
        Assert.Equal("repair", command.Name);
        Assert.Equal(RunMode.Sync, command.RunMode);
        var parameter = Assert.Single(parameters);
        Assert.Equal(typeof(ITextChannel), parameter.ParameterType);
        Assert.True(parameter.HasDefaultValue);
        Assert.Null(parameter.DefaultValue);
    }

    [Fact]
    public void MutatingHandlers_UseSynchronousInteractionRunMode()
    {
        var methods = new[]
        {
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.HandleCreateModalAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.PublishAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.ConfirmMigrationAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.ChangeMigrationPageAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.SelectMigrationPanelAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.HandleEditModalAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.ChangeEditPageAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.SelectEditAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.ConfirmRepairAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.ChangeRepairPageAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.SelectRepairAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.ConfirmDeleteAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.ChangeDeletePageAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.SelectDeleteAsync)),
            typeof(RoleMenuAdminModule).GetMethod(nameof(RoleMenuAdminModule.CancelDeleteAsync)),
            typeof(RoleMenuMemberModule).GetMethod(nameof(RoleMenuMemberModule.SaveAsync)),
            typeof(RoleMenuMemberModule).GetMethod(nameof(RoleMenuMemberModule.ClearAsync))
        };

        foreach (var method in methods)
        {
            Assert.NotNull(method);
            var runMode = method.GetCustomAttribute<ComponentInteractionAttribute>()?.RunMode
                ?? method.GetCustomAttribute<ModalInteractionAttribute>()?.RunMode;
            Assert.True(runMode.HasValue);
            Assert.Equal(RunMode.Sync, runMode.Value);
        }
    }
}
