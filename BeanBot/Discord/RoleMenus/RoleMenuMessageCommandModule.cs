using Discord;
using Discord.Interactions;

namespace BeanBot.Discord.RoleMenus;

[CommandContextType(InteractionContextType.Guild)]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageRoles)]
[DefaultMemberPermissions(GuildPermission.ManageRoles)]
public sealed class RoleMenuMessageCommandModule : RoleMenuModuleBase
{
    public RoleMenuMessageCommandModule(RoleMenuInteractionService roleMenus)
        : base(roleMenus)
    {
    }

    [MessageCommand("Delete Role Menu", runMode: RunMode.Sync)]
    public async Task DeleteRoleMenuAsync(IMessage message)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => DeferAsync(
                ephemeral: true,
                new RequestOptions { CancelToken = operationToken }),
            operationToken => ReplaceResponseAsync("Checking the selected role menu…", operationToken),
            cancellation.Token);

        if (Context.Guild is null
            || Context.User is not IGuildUser administrator
            || !administrator.GuildPermissions.ManageRoles
            || message.Channel.Id != Context.Channel.Id)
        {
            await ReplaceResponseAsync("That role-menu message cannot be managed here.", cancellation.Token);
            return;
        }

        var guildId = Context.Guild.Id;
        var channelId = Context.Channel.Id;
        var settings = await RoleMenus.GetByMessageAsync(
            guildId, channelId, message.Id, cancellation.Token);
        if (!RoleMenuPanelIdentity.Matches(
                settings,
                guildId,
                channelId,
                message.Id,
                message.Author.Id,
                Context.Guild.CurrentUser.Id,
                message))
        {
            await ReplaceResponseAsync("That message is not a current Bean Bot role-menu panel.",
                cancellation.Token);
            return;
        }

        await ReplaceResponseAsync(
            "Confirm this destructive action.",
            cancellation.Token,
            RoleMenuComponents.BuildDeleteConfirmationEmbed(settings!, Context.Channel.Name),
            RoleMenuComponents.BuildDeleteConfirmationComponents(
                Context.User.Id, settings!.Id, channelId, message.Id));
    }
}
