using System.Reflection;
using BeanBot.Discord.Commands;
using BeanBot.Discord.ReactionRoles;
using Discord;
using Discord.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class AdministrativeModuleRoleAssignabilityTests
{
    [Fact]
    public async Task SendRoleValidationMessageAsync_EveryoneRejectionDisablesAllMentions()
    {
        string? emittedText = null;
        AllowedMentions? emittedMentions = null;
        var sentMessage = DispatchProxy.Create<IUserMessage, DiscordProxy>();
        var channel = DispatchProxy.Create<IMessageChannel, DiscordProxy>();
        ((DiscordProxy)channel).InvokeMethod = (method, arguments) =>
        {
            Assert.Equal(nameof(IMessageChannel.SendMessageAsync), method.Name);
            emittedText = Assert.IsType<string>(arguments[0]);
            var mentionIndex = Array.FindIndex(method.GetParameters(), parameter => parameter.Name == "allowedMentions");
            emittedMentions = Assert.IsType<AllowedMentions>(arguments[mentionIndex]);
            return Task.FromResult(sentMessage);
        };
        var context = DispatchProxy.Create<ICommandContext, DiscordProxy>();
        ((DiscordProxy)context).InvokeMethod = (method, _) => method.Name == "get_Channel"
            ? channel
            : throw new InvalidOperationException(method.Name);
        using var sender = new LegacyCommandReplySender(
            NullLogger<LegacyCommandReplySender>.Instance, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), CancellationToken.None);
        var text = AdministrativeModule.GetRoleValidationMessage(ReactionRoleAssignabilityStatus.EveryoneRole)!;

        Assert.Same(sentMessage, await AdministrativeModule.SendRoleValidationMessageAsync(sender, context, text));

        Assert.Contains("@everyone", emittedText);
        Assert.Same(AllowedMentions.None, emittedMentions);
    }

    public class DiscordProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?>? InvokeMethod { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => InvokeMethod!(targetMethod!, args ?? []);
    }

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
