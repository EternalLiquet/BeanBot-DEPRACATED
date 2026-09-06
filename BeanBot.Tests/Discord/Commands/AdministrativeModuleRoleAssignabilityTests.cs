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
    [InlineData((int)ReactionRoleAssignabilityStatus.EveryoneRole, "@everyone")]
    [InlineData((int)ReactionRoleAssignabilityStatus.ManagedRole, "managed")]
    [InlineData((int)ReactionRoleAssignabilityStatus.BotMissingManageRoles, "Manage Roles")]
    [InlineData((int)ReactionRoleAssignabilityStatus.BotHierarchyTooLow, "highest role")]
    [InlineData((int)ReactionRoleAssignabilityStatus.InvokerHierarchyTooLow, "below your highest role")]
    [InlineData((int)ReactionRoleAssignabilityStatus.RoleMissing, "no longer available")]
    public void GetRoleValidationMessage_Rejection_IsActionableAndSafe(
        int statusValue,
        string expectedText)
    {
        var status = (ReactionRoleAssignabilityStatus)statusValue;
        var message = AdministrativeModule.GetRoleValidationMessage(status);

        Assert.NotNull(message);
        Assert.Contains(expectedText, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission bit", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position=", message, StringComparison.OrdinalIgnoreCase);
    }
}
