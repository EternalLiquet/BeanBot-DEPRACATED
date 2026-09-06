using System.Reflection;
using BeanBot.Discord.Commands;
using BeanBot.Discord.ReactionRoles;
using Discord;
using Discord.Commands;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class AdministrativeModuleRoleAssignabilityTests
{
    [Fact]
    public void RoleSetting_RequiresBotManageRolesAndPanelReactionPermissions()
    {
        var method = typeof(AdministrativeModule).GetMethod(nameof(AdministrativeModule.RoleSetting));
        var attributes = Assert.IsAssignableFrom<IEnumerable<RequireBotPermissionAttribute>>(
            method?.GetCustomAttributes<RequireBotPermissionAttribute>() ?? []);

        Assert.Contains(attributes, attribute =>
            attribute.GuildPermission == GuildPermission.ManageRoles);
        Assert.Contains(attributes, attribute =>
            attribute.ChannelPermission == (ChannelPermission.EmbedLinks | ChannelPermission.AddReactions));
    }

    [Fact]
    public void GetRoleValidationMessage_Allowed_ReturnsNull()
    {
        Assert.Null(AdministrativeModule.GetRoleValidationMessage(ReactionRoleAssignabilityStatus.Allowed));
    }

    [Theory]
    [InlineData(ReactionRoleAssignabilityStatus.EveryoneRole, "@everyone")]
    [InlineData(ReactionRoleAssignabilityStatus.ManagedRole, "managed")]
    [InlineData(ReactionRoleAssignabilityStatus.BotMissingManageRoles, "Manage Roles")]
    [InlineData(ReactionRoleAssignabilityStatus.BotHierarchyTooLow, "highest role")]
    [InlineData(ReactionRoleAssignabilityStatus.InvokerHierarchyTooLow, "below your highest role")]
    [InlineData(ReactionRoleAssignabilityStatus.RoleMissing, "no longer available")]
    public void GetRoleValidationMessage_Rejection_IsActionableAndSafe(
        ReactionRoleAssignabilityStatus status,
        string expectedText)
    {
        var message = AdministrativeModule.GetRoleValidationMessage(status);

        Assert.NotNull(message);
        Assert.Contains(expectedText, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission bit", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position=", message, StringComparison.OrdinalIgnoreCase);
    }
}
