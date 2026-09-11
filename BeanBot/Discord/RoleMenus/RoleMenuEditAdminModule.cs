using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;

namespace BeanBot.Discord.RoleMenus;

public sealed partial class RoleMenuAdminModule
{
    [SlashCommand(
        "edit",
        "Edit a published role menu in place.",
        runMode: RunMode.Sync)]
    public async Task EditAsync(
        [Summary("menu-id", "Optional ID shown in the role panel footer")]
        string? menuId = null)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => DeferAsync(
                ephemeral: true,
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                "Loading role menus…",
                operationToken),
            cancellation.Token);
        if (Context.Guild is null)
        {
            await ReplaceResponseAsync(
                "Role menus can only be edited inside a server.",
                cancellation.Token);
            return;
        }

        if (!string.IsNullOrWhiteSpace(menuId))
        {
            if (!RoleMenuCustomIds.TryParseMenuId(menuId.Trim(), out var parsedMenuId))
            {
                await ReplaceResponseAsync(
                    "That menu ID is invalid. Copy the ID from the role panel footer.",
                    cancellation.Token);
                return;
            }

            var settings = await RoleMenus.GetAsync(
                parsedMenuId,
                Context.Guild.Id,
                cancellation.Token);
            if (settings is null)
            {
                await ReplaceResponseAsync(
                    "No saved role menu with that ID exists in this server.",
                    cancellation.Token);
                return;
            }

            await ShowEditPreviewAsync(settings, cancellation.Token);
            return;
        }

        var menus = await RoleMenus.GetByGuildAsync(
            Context.Guild.Id,
            RoleMenuConstants.MaximumListedMenus + 1,
            cancellation.Token);
        if (menus.Count == 0)
        {
            await ReplaceResponseAsync(
                "This server has no saved dropdown role menus.",
                cancellation.Token);
            return;
        }

        var hasMore = menus.Count > RoleMenuConstants.MaximumListedMenus;
        var listedMenus = menus.Take(RoleMenuConstants.MaximumListedMenus).ToList();
        await ReplaceResponseAsync(
            hasMore
                ? "Choose one of the 25 newest menus. For an older panel, rerun `/role-menu edit` " +
                  "with the ID shown in its footer."
                : "Choose the role menu you want to edit.",
            cancellation.Token,
            components: RoleMenuComponents.BuildEditSelector(Context.User.Id, listedMenus));
    }

    [ComponentInteraction(
        RoleMenuCustomIds.EditSelectPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task SelectEditAsync(string userIdValue, string[] selectedMenuIds)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || selectedMenuIds is not { Length: 1 }
            || !RoleMenuCustomIds.TryParseMenuId(selectedMenuIds[0], out var menuId)
            || Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidPrivateComponent(
                component,
                Context.Guild,
                ComponentType.SelectMenu,
                RoleMenuCustomIds.EditSelect(boundUserId),
                selectedMenuIds[0]))
        {
            await RespondToInvalidComponentAsync(
                "That edit selection is invalid or belongs to another administrator.",
                cancellation.Token);
            return;
        }

        if (!await AcknowledgeEphemeralComponentAsync(
                "Loading the selected role menu…",
                cancellation.Token))
        {
            return;
        }

        var settings = await RoleMenus.GetAsync(
            menuId,
            Context.Guild.Id,
            cancellation.Token);
        if (settings is null)
        {
            await ReplaceResponseAsync(
                "That role menu was deleted or no longer exists.",
                cancellation.Token);
            return;
        }

        await ShowEditPreviewAsync(settings, cancellation.Token);
    }

    [ComponentInteraction(
        RoleMenuCustomIds.EditOpenPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task OpenEditAsync(string draftIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseDraftId(draftIdValue, out var draftId)
            || Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidPrivateComponent(
                component,
                Context.Guild,
                ComponentType.Button,
                RoleMenuCustomIds.EditOpen(draftId)))
        {
            await RespondToInvalidComponentAsync(
                "That role-menu edit preview is invalid or no longer available.",
                cancellation.Token);
            return;
        }

        var accessStatus = RoleMenus.TryGetEditDraft(
            draftId,
            Context.Guild.Id,
            Context.User.Id,
            out var draft);
        if (accessStatus != RoleMenuEditDraftAccessStatus.Acquired || draft is null)
        {
            await RespondToInvalidComponentAsync(
                accessStatus == RoleMenuEditDraftAccessStatus.AlreadySubmitting
                    ? "That role-menu edit is already being submitted."
                    : "That role-menu edit preview expired or belongs to another administrator.",
                cancellation.Token);
            return;
        }

        var roles = draft.RoleIds
            .Select(Context.Guild.GetRole)
            .Where(role => role is not null)
            .Cast<IRole>()
            .ToArray();
        var modal = new RoleMenuEditModal
        {
            PanelTitle = draft.Title,
            Description = draft.Description,
            Roles = roles,
            SelectionMode = draft.SelectionMode == RoleMenuSelectionMode.Exclusive
                ? "single"
                : "multiple"
        };
        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: false,
            operationToken => RespondWithModalAsync(
                RoleMenuCustomIds.EditModal(draft.Id),
                modal,
                CreateRequestOptions(operationToken)),
            _ => Task.CompletedTask,
            cancellation.Token);
    }

    [ModalInteraction(
        RoleMenuCustomIds.EditModalPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task HandleEditModalAsync(string draftIdValue, RoleMenuEditModal modal)
    {
        ArgumentNullException.ThrowIfNull(modal);
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseDraftId(draftIdValue, out var draftId)
            || Context.Guild is null)
        {
            await RespondToInvalidComponentAsync(
                "That role-menu edit preview is invalid or no longer available.",
                cancellation.Token);
            return;
        }

        var accessStatus = RoleMenus.TryBeginEdit(
            draftId,
            Context.Guild.Id,
            Context.User.Id,
            out var draft);
        if (accessStatus != RoleMenuEditDraftAccessStatus.Acquired || draft is null)
        {
            await RespondToInvalidComponentAsync(
                accessStatus == RoleMenuEditDraftAccessStatus.AlreadySubmitting
                    ? "That role-menu edit is already being submitted."
                    : "That role-menu edit preview expired or belongs to another administrator.",
                cancellation.Token);
            return;
        }

        var completed = false;
        try
        {
            await RoleMenus.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => DeferAsync(
                    ephemeral: true,
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    "Saving the role-menu edit…",
                    operationToken),
                cancellation.Token);

            var bot = Context.Guild.CurrentUser;
            if (bot is null)
            {
                await ReplaceResponseAsync(
                    "Bean Bot couldn't resolve its current server identity. Try again.",
                    cancellation.Token);
                return;
            }

            var mutationStarted = false;
            RoleMenuEditResult result;
            try
            {
                result = await RoleMenus.RunMenuMutationAsync(
                    draft.MenuId,
                    operationToken =>
                    {
                        mutationStarted = true;
                        return _administration.EditAsync(
                            draft.MenuId,
                            new RoleMenuEditRequest(
                                modal.PanelTitle,
                                modal.Description,
                                modal.SelectionMode,
                                modal.Roles?.Select(role => role.Id).ToArray()),
                            Context.Guild.Id,
                            Context.User.Id,
                            bot.Id,
                            operationToken);
                    },
                    cancellation.Token);
            }
            catch (OperationCanceledException)
                when (cancellation.IsCancellationRequested && !RoleMenus.IsShuttingDown)
            {
                await SendFreshFeedbackAsync(
                    mutationStarted
                        ? "Bean Bot ran out of time while editing this menu and could not confirm the final state. Inspect the saved menu and existing panel before retrying; no replacement panel was published."
                        : "Bean Bot was busy and did not begin editing this role menu. Try again.");
                return;
            }

            await ReplaceResponseAsync(result.Content, cancellation.Token);
            if (result.Status == RoleMenuEditStatus.Updated)
            {
                RoleMenus.CompleteEdit(draft.Id, draft.GuildId, draft.UserId);
                completed = true;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuEditOperationFailed(
                _logger,
                draft.MenuId.ToString(),
                exception);
            await SendFreshFeedbackAsync(
                "Bean Bot couldn't confirm the role-menu edit. Inspect the saved menu and existing panel before retrying; no replacement panel was published.");
        }
        finally
        {
            if (!completed)
            {
                RoleMenus.ReleaseEdit(draft.Id, draft.GuildId, draft.UserId);
            }
        }
    }

    private async Task ShowEditPreviewAsync(
        RoleMenuSettings settings,
        CancellationToken cancellationToken)
    {
        if (!RoleMenuSettingsParser.TryParse(settings, out var parsed, out _))
        {
            await ReplaceResponseAsync(
                "That saved role menu is invalid and cannot be edited safely. Use `/role-menu delete` to clean it up.",
                cancellationToken);
            return;
        }

        var createStatus = RoleMenus.CreateEditDraft(
            settings.Id,
            parsed.GuildId,
            Context.User.Id,
            settings.Title,
            settings.Description,
            parsed.RoleIds,
            settings.SelectionMode,
            out var draft);
        if (createStatus != RoleMenuEditDraftCreateStatus.Created || draft is null)
        {
            await ReplaceResponseAsync(
                createStatus == RoleMenuEditDraftCreateStatus.AlreadySubmitting
                    ? "Your previous role-menu edit is still being submitted. Wait for it to finish."
                    : "Bean Bot is already holding the maximum number of role-menu edit previews. Try again after another preview expires.",
                cancellationToken);
            return;
        }

        await ReplaceResponseAsync(
            "Review the current values below. Select **Edit values** to open a pre-filled form; the saved menu is revalidated again when you submit it.",
            cancellationToken,
            RoleMenuComponents.BuildEditSummaryEmbed(draft),
            RoleMenuComponents.BuildEditOpenComponents(draft.Id));
    }
}
