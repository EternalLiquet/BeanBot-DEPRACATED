using BeanBot.Logging;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
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
    public async Task EditAsync()
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => DeferAsync(ephemeral: true, CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync("Loading role menus…", operationToken),
            cancellation.Token);
        if (Context.Guild is null)
        {
            await ReplaceResponseAsync("Role menus can only be edited inside a server.", cancellation.Token);
            return;
        }

        await ShowEditPageAsync(Context.Guild, null, cancellation.Token);
    }

    [ComponentInteraction(
        RoleMenuCustomIds.EditPagePattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ChangeEditPageAsync(
        string userIdValue,
        string directionValue,
        string createdAtTicksValue,
        string menuIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !RoleMenuCustomIds.TryParsePageCursor(
                directionValue, createdAtTicksValue, menuIdValue, out var cursor)
            || Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(
                component, Context.Guild, ComponentType.Button,
                RoleMenuCustomIds.EditPage(boundUserId, cursor)))
        {
            await RespondToInvalidComponentAsync(
                "This list has expired or belongs to someone else. Run `/role-menu edit` again.",
                cancellation.Token);
            return;
        }

        if (await AcknowledgeEphemeralComponentAsync("Loading role menus…", cancellation.Token))
            await ShowEditPageAsync(Context.Guild, cursor, cancellation.Token);
    }

    private async Task ShowEditPageAsync(
        SocketGuild guild,
        RoleMenuPageCursor? cursor,
        CancellationToken cancellationToken)
    {
        var page = await _administration.LoadDeletionPageAsync(guild.Id, cursor, cancellationToken);
        if (page.Menus.Count == 0)
        {
            await ReplaceResponseAsync("This server has no saved dropdown role menus.", cancellationToken);
            return;
        }

        await ReplaceResponseAsync(
            "Which role menu do you want to edit?",
            cancellationToken,
            components: RoleMenuComponents.BuildEditSelector(
                Context.User.Id, page, channelId => guild.GetChannel(channelId)?.Name));
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
            || !IsValidManagementComponent(
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
        Task<RoleMenuEditResult>? mutationTask = null;
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
            mutationTask = RoleMenus.RunMenuMutationAsync(
                draft.MenuId,
                operationToken =>
                {
                    mutationStarted = true;
                    return _administration.EditAsync(
                        draft,
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
            var result = await RoleMenuEditMutationWaiter.WaitAsync(
                mutationTask,
                () => RoleMenus.IsShuttingDown
                    ? Task.CompletedTask
                    : SendFreshFeedbackAsync(
                        mutationStarted
                            ? "I ran out of time while editing this menu and couldn't confirm the result. Check the saved menu and existing panel before retrying."
                            : "I was busy and didn't start editing this menu. Try again."),
                cancellation.Token);
            if (result is not null)
                await ReplaceResponseAsync(result.Content, cancellation.Token);
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
            // Wait for the real mutation, including a Discord or Mongo call that ignored
            // cancellation. The interaction tracker and instance lease keep ownership until then.
            if (mutationTask is not null)
            {
                try
                {
                    var settled = await mutationTask;
                    if (settled.Status == RoleMenuEditStatus.Updated)
                    {
                        RoleMenus.CompleteEdit(draft.Id, draft.GuildId, draft.UserId);
                        completed = true;
                    }
                }
                catch (Exception exception)
                {
                    BeanBotLog.RoleMenuEditOperationFailed(
                        _logger, draft.MenuId.ToString(), exception);
                }
            }
            if (!completed)
                RoleMenus.ReleaseEdit(draft.Id, draft.GuildId, draft.UserId);
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
            out var draft,
            RoleMenuEditSnapshot.From(settings));
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
