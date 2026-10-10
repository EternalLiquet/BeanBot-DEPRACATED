using Discord;
using Discord.Interactions;
using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;

namespace BeanBot.Discord.RoleMenus;

/// <summary>
/// Right-click entry point for deleting a role menu. It only identifies the menu and shows the
/// same owner-bound confirmation as <c>/role-menu delete</c>; the confirmation buttons are handled
/// by <see cref="RoleMenuAdminModule"/>, which runs the existing deletion workflow.
/// </summary>
[CommandContextType(InteractionContextType.Guild)]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageRoles)]
[DefaultMemberPermissions(GuildPermission.ManageRoles)]
public sealed class RoleMenuMessageCommandModule : RoleMenuModuleBase
{
    internal const string DeleteCommandName = "Delete Role Menu";

    private readonly RoleMenuAdministrationService _administration;

    public RoleMenuMessageCommandModule(
        RoleMenuInteractionService roleMenus,
        RoleMenuAdministrationService administration)
        : base(roleMenus)
    {
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));
    }

    [MessageCommand(DeleteCommandName, runMode: RunMode.Sync)]
    public async Task DeleteRoleMenuAsync(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => DeferAsync(
                ephemeral: true,
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                "Loading that menu…",
                operationToken),
            cancellation.Token);

        if (Context.Guild is null
            || Context.User is not IGuildUser administrator
            || administrator.GuildId != Context.Guild.Id)
        {
            await ReplaceResponseAsync(
                "You can only delete role menus in a server.",
                cancellation.Token);
            return;
        }

        if (!administrator.GuildPermissions.ManageRoles)
        {
            await ReplaceResponseAsync(
                "You need the **Manage Roles** permission to delete role menus.",
                cancellation.Token);
            return;
        }

        var interactionChannelId = Context.Interaction.ChannelId ?? 0;
        var manageButtonMenuIds = RoleMenuDeletionTargets.GetManageButtonMenuIds(message);
        var issue = RoleMenuDeletionTargets.CheckMessage(
            interactionChannelId,
            Context.Guild.CurrentUser.Id,
            message.Channel?.Id ?? 0,
            message.Author.Id,
            manageButtonMenuIds);
        var settings = issue == RoleMenuMessageTargetIssue.None
            ? await _administration.FindMenuForMessageAsync(
                Context.Guild.Id,
                interactionChannelId,
                message.Id,
                manageButtonMenuIds,
                cancellation.Token)
            : null;
        if (settings is null)
        {
            await ReplaceResponseAsync(
                issue == RoleMenuMessageTargetIssue.None
                    ? "I couldn't find that role menu. It may have been deleted already."
                    : "That message isn't one of my role menus.",
                cancellation.Token);
            return;
        }

        // The message in hand is the panel itself, so it counts as current; the confirmation
        // still rereads the saved menu and the message before deleting anything.
        const RoleMenuPanelState panelState = RoleMenuPanelState.Current;
        await ReplaceResponseAsync(
            RoleMenuComponents.FormatDeleteConfirmationContent(panelState),
            cancellation.Token,
            RoleMenuComponents.BuildDeleteConfirmationEmbed(settings, panelState),
            RoleMenuComponents.BuildDeleteConfirmationComponents(
                Context.User.Id,
                settings,
                panelState));
    }
}
