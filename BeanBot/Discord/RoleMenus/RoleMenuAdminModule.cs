using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuPresentation;
using static BeanBot.Discord.RoleMenus.RoleMenuSetupValidation;

namespace BeanBot.Discord.RoleMenus;

[Group("role-menu", "Create and remove self-assignable role menus.")]
[CommandContextType(InteractionContextType.Guild)]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageRoles)]
[DefaultMemberPermissions(GuildPermission.ManageRoles)]
public sealed class RoleMenuAdminModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly RoleMenuInteractionService _roleMenuService;
    private readonly DiscordRoleMenuClient _discord;
    private readonly ILogger<RoleMenuAdminModule> _logger;

    public RoleMenuAdminModule(
        RoleMenuInteractionService roleMenuService,
        DiscordRoleMenuClient discord,
        ILogger<RoleMenuAdminModule> logger)
    {
        _roleMenuService = roleMenuService ?? throw new ArgumentNullException(nameof(roleMenuService));
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [SlashCommand(
        "create",
        "Create a native dropdown panel for self-assignable roles.",
        runMode: RunMode.Sync)]
    public async Task CreateAsync()
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        await _roleMenuService.ExecuteInitialResponseAsync(
            supportsOriginalResponse: false,
            operationToken => RespondWithModalAsync<RoleMenuCreateModal>(
                RoleMenuCustomIds.CreateModal,
                CreateRequestOptions(operationToken)),
            _ => Task.CompletedTask,
            cancellation.Token);
    }

    [ModalInteraction(
        RoleMenuCustomIds.CreateModal,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task HandleCreateModalAsync(RoleMenuCreateModal modal)
    {
        ArgumentNullException.ThrowIfNull(modal);
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        var requestOptions = CreateRequestOptions(cancellation.Token);
        await _roleMenuService.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => DeferAsync(
                ephemeral: true,
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                "Loading the role-menu preview…",
                operationToken),
            cancellation.Token);

        if (!TryGetGuildActors(out var guild, out var administrator, out var bot))
        {
            await ReplaceResponseAsync(
                "Role menus can only be created inside a server.",
                cancellation.Token);
            return;
        }

        var currentAdministrator = await _discord.GetGuildUserAsync(
            guild.Id,
            administrator.Id,
            requestOptions);
        var currentBot = await _discord.GetGuildUserAsync(guild.Id, bot.Id, requestOptions);
        if (currentAdministrator is null || currentBot is null)
        {
            await ReplaceResponseAsync(
                "Bean Bot couldn't refresh the current server role hierarchy. Try again in a moment.",
                cancellation.Token);
            return;
        }

        var title = modal.PanelTitle.Trim();
        var description = modal.Description?.Trim() ?? string.Empty;
        if (!TryParseAndValidateModal(
                modal,
                guild,
                currentAdministrator,
                currentBot,
                title,
                description,
                out var targetChannelId,
                out var selectionMode,
                out var roleValidation,
                out var validationMessage))
        {
            await ReplaceResponseAsync(validationMessage, cancellation.Token);
            return;
        }

        var targetChannel = await _discord.GetGuildTextChannelAsync(
            guild.Id,
            targetChannelId,
            requestOptions);
        if (targetChannel is null)
        {
            await ReplaceResponseAsync(
                "That target channel no longer exists in this server.",
                cancellation.Token);
            return;
        }

        var channelPermissionFailure = GetChannelPermissionFailure(currentBot, targetChannel);
        if (channelPermissionFailure is not null)
        {
            await ReplaceResponseAsync(channelPermissionFailure, cancellation.Token);
            return;
        }

        var createStatus = _roleMenuService.CreateDraft(
            guild.Id,
            administrator.Id,
            targetChannel.Id,
            title,
            description,
            roleValidation.Roles.Select(role => role.Id).ToList(),
            selectionMode,
            out var draft);
        if (createStatus != RoleMenuDraftCreateStatus.Created || draft is null)
        {
            await ReplaceResponseAsync(
                createStatus == RoleMenuDraftCreateStatus.AlreadyPublishing
                    ? "Your previous role menu is still publishing. Wait for it to finish before " +
                      "starting another preview."
                    : "Bean Bot is already holding the maximum number of role-menu previews. " +
                      "Try again after another preview expires.",
                cancellation.Token);
            return;
        }

        await ReplaceResponseAsync(
            "Review this private preview, then publish it when it looks right.",
            cancellation.Token,
            RoleMenuComponents.BuildPreviewEmbed(draft, roleValidation.Roles),
            RoleMenuComponents.BuildPreviewComponents(draft.Id));
    }

    [ComponentInteraction(
        RoleMenuCustomIds.PublishPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task PublishAsync(string draftIdValue)
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        var requestOptions = CreateRequestOptions(cancellation.Token);
        if (!RoleMenuCustomIds.TryParseDraftId(draftIdValue, out var draftId)
            || !TryGetGuildActors(out var guild, out var administrator, out var bot)
            || Context.Interaction is not SocketMessageComponent sourceComponent
            || !IsValidPrivateComponent(
                sourceComponent,
                guild,
                ComponentType.Button,
                RoleMenuCustomIds.Publish(draftId)))
        {
            await RespondToInvalidComponentAsync(
                "That role-menu preview is invalid or no longer available.",
                cancellation.Token);
            return;
        }

        var accessStatus = _roleMenuService.TryBeginPublish(
            draftId,
            guild.Id,
            administrator.Id,
            out var draft);
        if (accessStatus != RoleMenuDraftAccessStatus.Acquired || draft is null)
        {
            var message = accessStatus switch
            {
                RoleMenuDraftAccessStatus.AlreadyPublishing =>
                    "That preview is already being published.",
                RoleMenuDraftAccessStatus.WrongOwner =>
                    "Only the administrator who created this preview can publish it.",
                _ => "That role-menu preview expired or no longer exists. Run `/role-menu create` again."
            };
            await RespondToInvalidComponentAsync(
                message,
                cancellation.Token);
            return;
        }

        IReadOnlyCollection<RoleMenuRoleSnapshot> previewRoles = [];
        var publicationStarted = false;
        try
        {
            if (!await AcknowledgeEphemeralComponentAsync(
                    "Publishing the role menu…",
                    cancellation.Token))
            {
                return;
            }

            var currentAdministrator = await _discord.GetGuildUserAsync(
                guild.Id,
                administrator.Id,
                requestOptions);
            var currentBot = await _discord.GetGuildUserAsync(guild.Id, bot.Id, requestOptions);
            if (currentAdministrator is null || currentBot is null)
            {
                await RestorePreviewAsync(
                    draft,
                    [],
                    "Bean Bot couldn't refresh the current server role hierarchy. Try again.",
                    cancellation.Token);
                return;
            }

            var validation = ValidateDraftRoles(
                draft,
                currentAdministrator,
                currentBot);
            previewRoles = validation.Roles;
            if (!validation.IsValid)
            {
                await RestorePreviewAsync(
                    draft,
                    validation.Roles,
                    FormatRoleValidationFailure(validation),
                    cancellation.Token);
                return;
            }

            var targetChannel = await _discord.GetGuildTextChannelAsync(
                guild.Id,
                draft.TargetChannelId,
                requestOptions);
            if (targetChannel is null)
            {
                await RestorePreviewAsync(
                    draft,
                    validation.Roles,
                    "The selected target channel was deleted. Run `/role-menu create` again.",
                    cancellation.Token);
                return;
            }

            var channelPermissionFailure = GetChannelPermissionFailure(currentBot, targetChannel);
            if (channelPermissionFailure is not null)
            {
                await RestorePreviewAsync(
                    draft,
                    validation.Roles,
                    channelPermissionFailure,
                    cancellation.Token);
                return;
            }

            var publishedMessageId = await _roleMenuService.RunMenuMutationAsync(
                draft.MenuId,
                operationToken =>
                {
                    publicationStarted = true;
                    return PublishMenuUnderLockAsync(
                        draft,
                        validation.Roles,
                        targetChannel,
                        currentBot.Id,
                        guild.Id,
                        administrator.Id,
                        operationToken);
                },
                cancellation.Token);
            if (publishedMessageId is ulong messageId)
            {
                await TrySendPublicationConfirmationAsync(
                    guild.Id,
                    draft.TargetChannelId,
                    messageId,
                    draft.MenuId,
                    cancellation.Token);
            }
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !_roleMenuService.IsShuttingDown)
        {
            await RestorePreviewFreshAsync(
                draft,
                previewRoles,
                publicationStarted
                    ? "Bean Bot ran out of time and could not confirm the publication result. Check " +
                      "the target channel; retrying this preview safely reuses the same menu ID."
                    : "Bean Bot was busy and did not begin publishing this role menu. Try this " +
                      "preview again.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationFailed(_logger, draft.Id.ToString("N"), exception);
            await RestorePreviewFreshAsync(
                draft,
                previewRoles,
                "Bean Bot couldn't publish this role menu. The preview was kept so you can retry.");
        }
        finally
        {
            _roleMenuService.ReleasePublish(draft.Id, guild.Id, administrator.Id);
        }
    }

    [ComponentInteraction(
        RoleMenuCustomIds.CancelPublishPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task CancelPublishAsync(string draftIdValue)
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseDraftId(draftIdValue, out var draftId)
            || Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidPrivateComponent(
                component,
                Context.Guild,
                ComponentType.Button,
                RoleMenuCustomIds.CancelPublish(draftId))
            || !_roleMenuService.CancelDraft(draftId, Context.Guild.Id, Context.User.Id))
        {
            await RespondToInvalidComponentAsync(
                "That preview expired, belongs to another administrator, or is already publishing.",
                cancellation.Token);
            return;
        }

        if (Context.Interaction is SocketMessageComponent validComponent)
        {
            await _roleMenuService.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => validComponent.UpdateAsync(
                    properties => SetMessage(
                        properties,
                        "Role-menu creation cancelled.",
                        null,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    "Role-menu creation cancelled.",
                    operationToken),
                cancellation.Token);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Role-menu creation cancelled.",
            cancellation.Token);
    }

    [SlashCommand(
        "delete",
        "Delete a published role menu and its saved configuration.",
        runMode: RunMode.Sync)]
    public async Task DeleteAsync(
        [Summary("menu-id", "Optional ID shown in the role panel footer")]
        string? menuId = null)
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        await _roleMenuService.ExecuteInitialResponseAsync(
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
                "Role menus can only be deleted inside a server.",
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

            var settings = await _roleMenuService.GetAsync(
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

            await ShowDeleteConfirmationAsync(settings, cancellation.Token);
            return;
        }

        var menus = await _roleMenuService.GetByGuildAsync(
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
                ? "Choose one of the 25 newest menus. For an older panel, rerun `/role-menu delete` " +
                  "with the ID shown in its footer."
                : "Choose the role menu you want to delete.",
            cancellation.Token,
            components: RoleMenuComponents.BuildDeleteSelector(Context.User.Id, listedMenus));
    }

    [ComponentInteraction(
        RoleMenuCustomIds.DeleteSelectPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task SelectDeleteAsync(string userIdValue, string[] selectedMenuIds)
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
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
                RoleMenuCustomIds.DeleteSelect(boundUserId),
                selectedMenuIds[0]))
        {
            await RespondToInvalidComponentAsync(
                "That deletion selection is invalid or belongs to another administrator.",
                cancellation.Token);
            return;
        }

        if (!await AcknowledgeEphemeralComponentAsync(
                "Loading the selected role menu…",
                cancellation.Token))
        {
            return;
        }

        var settings = await _roleMenuService.GetAsync(
            menuId,
            Context.Guild.Id,
            cancellation.Token);
        if (settings is null)
        {
            await ReplaceResponseAsync(
                "That role menu was already deleted or no longer exists.",
                cancellation.Token);
            return;
        }

        await ShowDeleteConfirmationAsync(settings, cancellation.Token);
    }

    [ComponentInteraction(
        RoleMenuCustomIds.DeleteConfirmPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ConfirmDeleteAsync(string userIdValue, string menuIdValue)
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !RoleMenuCustomIds.TryParseMenuId(menuIdValue, out var menuId)
            || !TryGetGuildActors(out var guild, out _, out var bot)
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidPrivateComponent(
                component,
                guild,
                ComponentType.Button,
                RoleMenuCustomIds.DeleteConfirm(boundUserId, menuId)))
        {
            await RespondToInvalidComponentAsync(
                "That deletion confirmation is invalid or belongs to another administrator.",
                cancellation.Token);
            return;
        }

        if (!await AcknowledgeEphemeralComponentAsync(
                "Deleting the role menu…",
                cancellation.Token))
        {
            return;
        }

        RoleMenuDeletionResult result;
        var mutationStarted = false;
        try
        {
            result = await _roleMenuService.RunMenuMutationAsync(
                menuId,
                operationToken =>
                {
                    mutationStarted = true;
                    return DeleteMenuCoreAsync(
                        menuId,
                        guild,
                        bot,
                        Context.User.Id,
                        operationToken);
                },
                cancellation.Token);
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !_roleMenuService.IsShuttingDown)
        {
            await SendFreshFeedbackAsync(
                mutationStarted
                    ? "Bean Bot ran out of time while deleting this menu and couldn't confirm the " +
                      "final result. Run `/role-menu delete` again to inspect and finish cleanup."
                    : "Bean Bot was busy and did not begin deleting this role menu. Try again.");
            return;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuDeletionFailed(_logger, menuId.ToString(), exception);
            await SendFreshFeedbackAsync(
                "Bean Bot couldn't confirm the deletion result. Run `/role-menu delete` again to " +
                "inspect and finish cleanup.");
            return;
        }

        var response = result switch
        {
            { AuthorizationDenied: true } =>
                "You no longer have the **Manage Roles** permission required to delete role menus.",
            { PanelStatus: RoleMenuPanelDeletionStatus.Failed } =>
                "The published panel is still present, so its saved configuration was kept. Fix " +
                "the channel permissions and retry.",
            { PanelStatus: RoleMenuPanelDeletionStatus.OutcomeUnknown } =>
                "Bean Bot couldn't confirm whether the published panel was deleted, so its saved " +
                "configuration was kept. Retry this command to finish cleanup safely.",
            {
                PanelStatus: RoleMenuPanelDeletionStatus.UnexpectedMessage,
                ConfigurationStatus: RoleMenuConfigurationDeletionStatus.Kept
            } =>
                "The referenced message no longer looked like Bean Bot's panel and was left " +
                "untouched, but the saved configuration could not be deleted. Retry to finish cleanup.",
            {
                PanelStatus: RoleMenuPanelDeletionStatus.UnexpectedMessage,
                ConfigurationStatus: RoleMenuConfigurationDeletionStatus.OutcomeUnknown
            } =>
                "The referenced message no longer looked like Bean Bot's panel and was left " +
                "untouched. Bean Bot couldn't confirm whether the saved configuration was deleted; " +
                "run this command again to check.",
            { PanelStatus: RoleMenuPanelDeletionStatus.UnexpectedMessage } =>
                "The saved configuration was deleted, but the referenced message no longer looked like " +
                "Bean Bot's panel and was left untouched.",
            { ConfigurationStatus: RoleMenuConfigurationDeletionStatus.Kept } =>
                "The published panel is gone, but Bean Bot couldn't delete the saved configuration. " +
                "Retry this command to finish cleanup.",
            { ConfigurationStatus: RoleMenuConfigurationDeletionStatus.OutcomeUnknown } =>
                "The published panel is gone, but Bean Bot couldn't confirm whether its saved " +
                "configuration was deleted. Run this command again to check.",
            { ConfigurationStatus: RoleMenuConfigurationDeletionStatus.AlreadyMissing } =>
                "That role menu was already deleted.",
            _ => "Role menu and saved configuration deleted."
        };
        await SendFreshFeedbackAsync(response);
    }

    [ComponentInteraction(
        RoleMenuCustomIds.DeleteCancelPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task CancelDeleteAsync(string userIdValue)
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        var isOwner = RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            && boundUserId == Context.User.Id
            && Context.Guild is not null
            && Context.Interaction is SocketMessageComponent component
            && IsValidPrivateComponent(
                component,
                Context.Guild,
                ComponentType.Button,
                RoleMenuCustomIds.DeleteCancel(boundUserId));
        if (!isOwner)
        {
            await RespondToInvalidComponentAsync(
                "That deletion confirmation belongs to another administrator.",
                cancellation.Token);
            return;
        }

        if (Context.Interaction is SocketMessageComponent validComponent)
        {
            await _roleMenuService.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => validComponent.UpdateAsync(
                    properties => SetMessage(
                        properties,
                        "Role-menu deletion cancelled.",
                        null,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    "Role-menu deletion cancelled.",
                    operationToken),
                cancellation.Token);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Role-menu deletion cancelled.",
            cancellation.Token);
    }

    private bool TryGetGuildActors(
        out SocketGuild guild,
        out IGuildUser administrator,
        out SocketGuildUser bot)
    {
        guild = Context.Guild!;
        administrator = (Context.User as IGuildUser)!;
        bot = Context.Guild?.CurrentUser!;
        return guild is not null && administrator is not null && bot is not null;
    }

    private async Task<bool> AcknowledgeEphemeralComponentAsync(
        string loadingMessage,
        CancellationToken cancellationToken)
    {
        if (Context.Interaction is not SocketMessageComponent component
            || !IsEphemeral(component))
        {
            await RespondToInvalidComponentAsync(
                "That private role-menu control is invalid or expired.",
                cancellationToken);
            return false;
        }

        await _roleMenuService.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => component.UpdateAsync(
                properties => SetMessage(
                    properties,
                    loadingMessage,
                    null,
                    MessageComponent.Empty),
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                loadingMessage,
                operationToken),
            cancellationToken);
        return true;
    }

    private async Task RespondToInvalidComponentAsync(
        string message,
        CancellationToken cancellationToken)
    {
        if (Context.Interaction is SocketMessageComponent component
            && IsEphemeral(component))
        {
            await _roleMenuService.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => component.UpdateAsync(
                    properties => SetMessage(
                        properties,
                        message,
                        null,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(message, operationToken),
                cancellationToken);
            return;
        }

        if (Context.Interaction is SocketMessageComponent publicComponent)
        {
            await _roleMenuService.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => publicComponent.DeferLoadingAsync(
                    ephemeral: true,
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(message, operationToken),
                cancellationToken);
            await ReplaceResponseAsync(message, cancellationToken);
            return;
        }

        await _roleMenuService.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => RespondAsync(
                message,
                ephemeral: true,
                allowedMentions: AllowedMentions.None,
                options: CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(message, operationToken),
            cancellationToken);
    }

    private Task<IUserMessage> RestorePreviewAsync(
        RoleMenuDraft draft,
        IReadOnlyCollection<RoleMenuRoleSnapshot> roles,
        string errorMessage,
        CancellationToken cancellationToken)
        => ReplaceResponseAsync(
            errorMessage,
            cancellationToken,
            RoleMenuComponents.BuildPreviewEmbed(draft, roles),
            RoleMenuComponents.BuildPreviewComponents(draft.Id));

    private async Task RestorePreviewFreshAsync(
        RoleMenuDraft draft,
        IReadOnlyCollection<RoleMenuRoleSnapshot> roles,
        string errorMessage)
    {
        if (_roleMenuService.IsShuttingDown)
        {
            throw new OperationCanceledException();
        }

        using var feedbackCancellation = _roleMenuService.CreateFeedbackCancellation();
        await RestorePreviewAsync(
            draft,
            roles,
            errorMessage,
            feedbackCancellation.Token);
    }

    private async Task<ulong?> PublishMenuUnderLockAsync(
        RoleMenuDraft draft,
        IReadOnlyCollection<RoleMenuRoleSnapshot> roles,
        ITextChannel targetChannel,
        ulong botUserId,
        ulong guildId,
        ulong administratorId,
        CancellationToken cancellationToken)
    {
        var result = await RoleMenuPublicationWorkflow.ExecuteAsync(
            draft,
            botUserId,
            CreatePublicationOperations(targetChannel, draft.MenuId),
            cancellationToken);
        LogPublicationFailures(draft.MenuId, result.Failures);
        if (result.Status == RoleMenuPublicationStatus.Published)
        {
            _roleMenuService.CompletePublish(draft.Id, guildId, administratorId);
            return result.MessageId
                   ?? throw new InvalidOperationException(
                       "A published role menu did not return its panel message ID.");
        }

        if (result.CanRetry)
        {
            await RestorePreviewFreshAsync(
                draft,
                roles,
                "Bean Bot confirmed the settings were not saved and removed the panel. " +
                "You can retry this preview safely.");
            return null;
        }

        _roleMenuService.CompletePublish(draft.Id, guildId, administratorId);
        BeanBotLog.RoleMenuConfigurationInvalid(
            _logger,
            draft.MenuId.ToString(),
            $"publication ended in terminal state {result.Status}");
        var message = result.Status switch
        {
            RoleMenuPublicationStatus.PanelOutcomeUnknown =>
                "Discord reported an error while publishing, and Bean Bot could not confirm " +
                "whether a panel was created. Automatic retry was disabled to prevent a duplicate. " +
                "Check the target channel and remove any orphaned panel before running " +
                "`/role-menu create` again.",
            RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed =>
                "Bean Bot confirmed the settings were not saved but could not remove the panel. " +
                "Automatic retry was disabled to prevent a duplicate. Delete that orphaned panel " +
                "manually before running `/role-menu create` again.",
            _ =>
                "Bean Bot could not confirm whether MongoDB saved this panel. The public panel was " +
                "left in place to avoid deleting a possibly committed menu, and automatic retry " +
                "was disabled to prevent a duplicate. Inspect the target channel before running " +
                "`/role-menu create` again."
        };
        await SendTerminalPublicationFeedbackAsync(draft.MenuId, message);
        return null;
    }

    private void LogPublicationFailures(
        ObjectId menuId,
        IReadOnlyCollection<RoleMenuPublicationFailure> failures)
    {
        foreach (var failure in failures)
        {
            switch (failure.Phase)
            {
                case RoleMenuPublicationFailurePhase.PanelReconciliation:
                    BeanBotLog.RoleMenuPanelReconciliationFailed(
                        _logger,
                        menuId.ToString(),
                        failure.Exception);
                    break;
                case RoleMenuPublicationFailurePhase.PersistenceReconciliation:
                    BeanBotLog.RoleMenuPersistenceReconciliationFailed(
                        _logger,
                        menuId.ToString(),
                        failure.Exception);
                    break;
                case RoleMenuPublicationFailurePhase.PanelRollback:
                    BeanBotLog.RoleMenuPublicationRollbackFailed(
                        _logger,
                        menuId.ToString(),
                        failure.Exception);
                    break;
                default:
                    BeanBotLog.RoleMenuPublicationFailed(
                        _logger,
                        menuId.ToString(),
                        failure.Exception);
                    break;
            }
        }
    }

    private RoleMenuPublicationOperations CreatePublicationOperations(
        ITextChannel targetChannel,
        ObjectId menuId)
        => new(
            (id, guildId, cancellationToken) => _roleMenuService.GetAsync(
                id,
                guildId,
                cancellationToken),
            (channelId, messageId, cancellationToken) =>
                ReadPublicationPanelAsync(
                    targetChannel,
                    channelId,
                    messageId,
                    menuId,
                    cancellationToken),
            (channelId, maximumResults, cancellationToken) =>
                ReadRecentPublicationPanelsAsync(
                    targetChannel,
                    channelId,
                    maximumResults,
                    menuId,
                    cancellationToken),
            (draft, cancellationToken) => SendPublicationPanelAsync(
                targetChannel,
                draft,
                cancellationToken),
            (settings, cancellationToken) =>
                _roleMenuService.UpsertAsync(settings, cancellationToken),
            (panel, cancellationToken) => RollbackPublicationPanelAsync(
                targetChannel,
                panel,
                menuId,
                cancellationToken));

    private async Task TrySendPublicationConfirmationAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ObjectId menuId,
        CancellationToken operationCancellationToken)
    {
        var content = $"Role menu published: {CreateMessageUrl(guildId, channelId, messageId)}";
        if (!operationCancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReplaceResponseAsync(content, operationCancellationToken);
                return;
            }
            catch (Exception exception)
            {
                BeanBotLog.RoleMenuPublicationConfirmationFailed(
                    _logger,
                    menuId.ToString(),
                    exception);
            }
        }

        if (_roleMenuService.IsShuttingDown)
        {
            return;
        }

        using var feedbackCancellation = _roleMenuService.CreateFeedbackCancellation();
        try
        {
            await ReplaceResponseAsync(content, feedbackCancellation.Token);
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationConfirmationFailed(
                _logger,
                menuId.ToString(),
                exception);
        }
    }

    private async Task ShowDeleteConfirmationAsync(
        RoleMenuSettings settings,
        CancellationToken cancellationToken)
        => await ReplaceResponseAsync(
            "Confirm this destructive action.",
            cancellationToken,
            RoleMenuComponents.BuildDeleteConfirmationEmbed(settings),
            RoleMenuComponents.BuildDeleteConfirmationComponents(
                Context.User.Id,
                settings.Id));

    private async Task<RoleMenuDeletionResult> DeleteMenuCoreAsync(
        ObjectId menuId,
        SocketGuild guild,
        SocketGuildUser bot,
        ulong administratorId,
        CancellationToken cancellationToken)
    {
        var currentAdministrator = await _discord.GetGuildUserAsync(
            guild.Id,
            administratorId,
            CreateRequestOptions(cancellationToken));
        var result = await RoleMenuDeletionWorkflow.ExecuteAsync(
            menuId,
            guild.Id,
            bot.Id,
            currentAdministrator?.GuildPermissions.ManageRoles == true,
            CreateDeletionOperations(guild, menuId),
            cancellationToken);
        LogDeletionFailures(menuId, result.Failures);
        if ((result.PanelStatus is RoleMenuPanelDeletionStatus.Failed
                or RoleMenuPanelDeletionStatus.OutcomeUnknown)
            && result.Failures.Count == 0)
        {
            BeanBotLog.RoleMenuPanelDeletionFailed(
                _logger,
                menuId.ToString(),
                new InvalidOperationException(
                    $"Panel deletion stopped with issue {result.PanelIssue}."));
        }

        return result;
    }

    private void LogDeletionFailures(
        ObjectId menuId,
        IReadOnlyCollection<RoleMenuDeletionFailure> failures)
    {
        foreach (var failure in failures)
        {
            switch (failure.Phase)
            {
                case RoleMenuDeletionFailurePhase.PanelReconciliation:
                    BeanBotLog.RoleMenuPanelDeletionReconciliationFailed(
                        _logger,
                        menuId.ToString(),
                        failure.Exception);
                    break;
                case RoleMenuDeletionFailurePhase.PersistenceDeletion:
                    BeanBotLog.RoleMenuPersistenceDeletionFailed(
                        _logger,
                        menuId.ToString(),
                        failure.Exception);
                    break;
                case RoleMenuDeletionFailurePhase.PersistenceReconciliation:
                    BeanBotLog.RoleMenuDeletionReconciliationFailed(
                        _logger,
                        menuId.ToString(),
                        failure.Exception);
                    break;
                default:
                    BeanBotLog.RoleMenuPanelDeletionFailed(
                        _logger,
                        menuId.ToString(),
                        failure.Exception);
                    break;
            }
        }
    }

    private RoleMenuDeletionOperations CreateDeletionOperations(
        SocketGuild guild,
        ObjectId menuId)
        => new(
            (id, guildId, cancellationToken) => _roleMenuService.GetAsync(
                id,
                guildId,
                cancellationToken),
            (expectedMenuId, channelId, messageId, cancellationToken) =>
                _discord.ReadDeletionPanelAsync(
                    guild.Id,
                    expectedMenuId,
                    channelId,
                    messageId,
                    cancellationToken),
            (panel, cancellationToken) => _discord.DeleteDeletionPanelAsync(
                    guild.Id,
                menuId,
                panel,
                cancellationToken),
            (id, guildId, cancellationToken) => _roleMenuService.DeleteAsync(
                id,
                guildId,
                cancellationToken),
            () => _roleMenuService.IsShuttingDown);

    private async Task SendFreshFeedbackAsync(string content)
    {
        if (_roleMenuService.IsShuttingDown)
        {
            throw new OperationCanceledException();
        }

        using var feedbackCancellation = _roleMenuService.CreateFeedbackCancellation();
        await ReplaceResponseAsync(content, feedbackCancellation.Token);
    }

    private async Task SendTerminalPublicationFeedbackAsync(
        ObjectId menuId,
        string content)
    {
        try
        {
            await SendFreshFeedbackAsync(content);
        }
        catch (OperationCanceledException) when (_roleMenuService.IsShuttingDown)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationFailed(_logger, menuId.ToString(), exception);
        }
    }

    private Task<IUserMessage> ReplaceResponseAsync(
        string content,
        CancellationToken cancellationToken,
        Embed? embed = null,
        MessageComponent? components = null)
        => ModifyOriginalResponseAsync(
            properties => SetMessage(
                properties,
                content,
                embed,
                components ?? MessageComponent.Empty),
            CreateRequestOptions(cancellationToken));

    private static void SetMessage(
        MessageProperties properties,
        string content,
        Embed? embed,
        MessageComponent components)
    {
        properties.Content = content;
        Embed[] embeds = embed is null ? [] : [embed];
        properties.Embeds = embeds;
        properties.Components = components;
        properties.AllowedMentions = AllowedMentions.None;
    }

    private static bool IsEphemeral(SocketMessageComponent component)
        => component.Message.Flags?.HasFlag(MessageFlags.Ephemeral) == true;

    private static bool IsValidPrivateComponent(
        SocketMessageComponent component,
        SocketGuild guild,
        ComponentType expectedType,
        string expectedCustomId,
        string? selectedValue = null)
    {
        if (!IsEphemeral(component)
            || component.Message.Author.Id != guild.CurrentUser.Id
            || component.Data.Type != expectedType
            || !string.Equals(
                component.Data.CustomId,
                expectedCustomId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var sourceComponent = component.Message.Components
            .OfType<ActionRowComponent>()
            .SelectMany(row => row.Components)
            .OfType<IInteractableComponent>()
            .FirstOrDefault(candidate => candidate.Type == expectedType
                                         && string.Equals(
                                             candidate.CustomId,
                                             expectedCustomId,
                                             StringComparison.Ordinal));
        return sourceComponent is not null
               && (selectedValue is null
                   || sourceComponent is SelectMenuComponent selector
                   && selector.Options.Any(option => string.Equals(
                       option.Value,
                       selectedValue,
                       StringComparison.Ordinal)));
    }

}
