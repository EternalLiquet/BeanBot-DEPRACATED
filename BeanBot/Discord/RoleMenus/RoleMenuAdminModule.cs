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

namespace BeanBot.Discord.RoleMenus;

[Group("role-menu", "Create and remove self-assignable role menus.")]
[CommandContextType(InteractionContextType.Guild)]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageRoles)]
[DefaultMemberPermissions(GuildPermission.ManageRoles)]
public sealed class RoleMenuAdminModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly RoleMenuInteractionService _roleMenuService;
    private readonly ILogger<RoleMenuAdminModule> _logger;

    public RoleMenuAdminModule(
        RoleMenuInteractionService roleMenuService,
        ILogger<RoleMenuAdminModule> logger)
    {
        _roleMenuService = roleMenuService ?? throw new ArgumentNullException(nameof(roleMenuService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [SlashCommand(
        "create",
        "Create a native dropdown panel for self-assignable roles.",
        runMode: RunMode.Sync)]
    public async Task CreateAsync()
    {
        using var cancellation = _roleMenuService.CreateOperationCancellation();
        await RespondWithModalAsync<RoleMenuCreateModal>(
            RoleMenuCustomIds.CreateModal,
            CreateRequestOptions(cancellation.Token));
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
        await DeferAsync(ephemeral: true, requestOptions);

        if (!TryGetGuildActors(out var guild, out var administrator, out var bot))
        {
            await ReplaceResponseAsync(
                "Role menus can only be created inside a server.",
                cancellation.Token);
            return;
        }

        var currentAdministrator = await GetGuildUserAsync(
            guild.Id,
            administrator.Id,
            requestOptions);
        var currentBot = await GetGuildUserAsync(guild.Id, bot.Id, requestOptions);
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

        var targetChannel = await ((IGuild)guild).GetTextChannelAsync(
            targetChannelId,
            CacheMode.AllowDownload,
            requestOptions);
        if (targetChannel is null || targetChannel.GuildId != guild.Id)
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
                requestOptions,
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
                requestOptions,
                cancellation.Token);
            return;
        }

        IReadOnlyCollection<RoleMenuRoleSnapshot> previewRoles = [];
        var publicationStarted = false;
        try
        {
            if (!await AcknowledgeEphemeralComponentAsync(
                    "Publishing the role menu…",
                    requestOptions,
                    cancellation.Token))
            {
                return;
            }

            var currentAdministrator = await GetGuildUserAsync(
                guild.Id,
                administrator.Id,
                requestOptions);
            var currentBot = await GetGuildUserAsync(guild.Id, bot.Id, requestOptions);
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

            var targetChannel = await ((IGuild)guild).GetTextChannelAsync(
                draft.TargetChannelId,
                CacheMode.AllowDownload,
                requestOptions);
            if (targetChannel is null || targetChannel.GuildId != guild.Id)
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
        var requestOptions = CreateRequestOptions(cancellation.Token);
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
                requestOptions,
                cancellation.Token);
            return;
        }

        if (Context.Interaction is SocketMessageComponent validComponent)
        {
            await validComponent.UpdateAsync(
                properties => SetMessage(
                    properties,
                    "Role-menu creation cancelled.",
                    null,
                    MessageComponent.Empty),
                requestOptions);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Role-menu creation cancelled.",
            requestOptions,
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
        var requestOptions = CreateRequestOptions(cancellation.Token);
        await DeferAsync(ephemeral: true, requestOptions);
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
        var requestOptions = CreateRequestOptions(cancellation.Token);
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
                requestOptions,
                cancellation.Token);
            return;
        }

        if (!await AcknowledgeEphemeralComponentAsync(
                "Loading the selected role menu…",
                requestOptions,
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
        var requestOptions = CreateRequestOptions(cancellation.Token);
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
                requestOptions,
                cancellation.Token);
            return;
        }

        if (!await AcknowledgeEphemeralComponentAsync(
                "Deleting the role menu…",
                requestOptions,
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
        var requestOptions = CreateRequestOptions(cancellation.Token);
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
                requestOptions,
                cancellation.Token);
            return;
        }

        if (Context.Interaction is SocketMessageComponent validComponent)
        {
            await validComponent.UpdateAsync(
                properties => SetMessage(
                    properties,
                    "Role-menu deletion cancelled.",
                    null,
                    MessageComponent.Empty),
                requestOptions);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Role-menu deletion cancelled.",
            requestOptions,
            cancellation.Token);
    }

    private static RequestOptions CreateRequestOptions(CancellationToken cancellationToken)
        => new() { CancelToken = cancellationToken };

    private async Task<IGuildUser?> GetGuildUserAsync(
        ulong guildId,
        ulong userId,
        RequestOptions requestOptions)
    {
        try
        {
            return await Context.Client.Rest.GetGuildUserAsync(
                guildId,
                userId,
                requestOptions);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
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

    private static bool TryParseAndValidateModal(
        RoleMenuCreateModal modal,
        SocketGuild guild,
        IGuildUser administrator,
        IGuildUser bot,
        string title,
        string description,
        out ulong targetChannelId,
        out RoleMenuSelectionMode selectionMode,
        [NotNullWhen(true)] out RoleMenuRoleValidationResult? roleValidation,
        out string validationMessage)
    {
        targetChannelId = 0;
        selectionMode = default;
        roleValidation = null;
        if (string.IsNullOrWhiteSpace(title)
            || title.Length > RoleMenuConstants.MaximumTitleLength)
        {
            validationMessage =
                $"The panel title must be 1–{RoleMenuConstants.MaximumTitleLength} characters.";
            return false;
        }

        if (description.Length > RoleMenuConstants.MaximumDescriptionLength)
        {
            validationMessage =
                $"The description cannot exceed {RoleMenuConstants.MaximumDescriptionLength} characters.";
            return false;
        }

        if (!TryParseSelectionMode(modal.SelectionMode, out selectionMode))
        {
            validationMessage = "Choose either single-selection or multiple-selection mode.";
            return false;
        }

        if (modal.TargetChannel is null
            || modal.TargetChannel.GuildId != guild.Id
            || modal.TargetChannel.ChannelType != ChannelType.Text)
        {
            validationMessage = "Choose a normal text channel from this server.";
            return false;
        }

        targetChannelId = modal.TargetChannel.Id;
        if (modal.Roles is not { Length: >= 1 and <= RoleMenuConstants.MaximumRoles })
        {
            validationMessage =
                $"Choose between 1 and {RoleMenuConstants.MaximumRoles} roles.";
            return false;
        }

        roleValidation = ValidateRoles(
            modal.Roles.Select(role => role.Id).ToList(),
            administrator,
            bot);
        if (!roleValidation.IsValid)
        {
            validationMessage = FormatRoleValidationFailure(roleValidation);
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    private static bool TryParseSelectionMode(
        string value,
        out RoleMenuSelectionMode selectionMode)
    {
        if (string.Equals(value, "multiple", StringComparison.Ordinal))
        {
            selectionMode = RoleMenuSelectionMode.Multiple;
            return true;
        }

        if (string.Equals(value, "single", StringComparison.Ordinal))
        {
            selectionMode = RoleMenuSelectionMode.Exclusive;
            return true;
        }

        selectionMode = default;
        return false;
    }

    private static RoleMenuRoleValidationResult ValidateDraftRoles(
        RoleMenuDraft draft,
        IGuildUser administrator,
        IGuildUser bot)
        => ValidateRoles(draft.RoleIds, administrator, bot);

    private static RoleMenuRoleValidationResult ValidateRoles(
        IReadOnlyCollection<ulong> roleIds,
        IGuildUser administrator,
        IGuildUser bot)
    {
        var availableRoles = bot.Guild.Roles
            .Select(role => new RoleMenuRoleSnapshot(
                role.Id,
                role.Name,
                role.Id == bot.Guild.EveryoneRole.Id,
                role.IsManaged,
                role.Position))
            .ToList();
        return RoleMenuRoleValidator.Validate(
            roleIds,
            availableRoles,
            CreateActorSnapshot(bot),
            CreateActorSnapshot(administrator));
    }

    private static RoleMenuActorSnapshot CreateActorSnapshot(IGuildUser user)
    {
        var hierarchy = user.Guild.Roles
            .Where(role => user.RoleIds.Contains(role.Id))
            .Select(role => role.Position)
            .DefaultIfEmpty(0)
            .Max();
        return new RoleMenuActorSnapshot(
            user.GuildPermissions.ManageRoles,
            hierarchy,
            user.Guild.OwnerId == user.Id);
    }

    private static string FormatRoleValidationFailure(
        RoleMenuRoleValidationResult validation)
    {
        var issue = validation.Issues[0];
        var roleName = string.IsNullOrWhiteSpace(issue.RoleName)
            ? "A selected role"
            : $"The role **{issue.RoleName}**";
        return issue.Kind switch
        {
            RoleMenuRoleIssueKind.BotMissingManageRoles =>
                "Bean Bot needs the **Manage Roles** permission before it can publish this menu.",
            RoleMenuRoleIssueKind.AdministratorMissingManageRoles =>
                "You no longer have the **Manage Roles** permission required to publish this menu.",
            RoleMenuRoleIssueKind.Duplicate =>
                $"{roleName} was selected more than once. Reopen the setup modal.",
            RoleMenuRoleIssueKind.Missing =>
                "A selected role was deleted or does not belong to this server.",
            RoleMenuRoleIssueKind.Everyone =>
                "The `@everyone` role cannot be self-assigned.",
            RoleMenuRoleIssueKind.Managed =>
                $"{roleName} is managed by Discord or an integration and cannot be assigned.",
            RoleMenuRoleIssueKind.BotHierarchy =>
                $"{roleName} is at or above Bean Bot's highest role. Move Bean Bot above it first.",
            RoleMenuRoleIssueKind.AdministratorHierarchy =>
                $"{roleName} is at or above your highest role and cannot be configured by you.",
            _ => "One or more selected roles cannot be assigned safely."
        };
    }

    private static string? GetChannelPermissionFailure(
        IGuildUser bot,
        ITextChannel targetChannel)
    {
        if (!bot.GuildPermissions.ManageRoles)
        {
            return "Bean Bot needs the **Manage Roles** permission before this menu can be published.";
        }

        var permissions = bot.GetPermissions(targetChannel);
        var missing = new List<string>();
        if (!permissions.ViewChannel)
        {
            missing.Add("View Channel");
        }

        if (!permissions.SendMessages)
        {
            missing.Add("Send Messages");
        }

        if (!permissions.EmbedLinks)
        {
            missing.Add("Embed Links");
        }

        if (!permissions.ReadMessageHistory)
        {
            missing.Add("Read Message History");
        }

        return missing.Count == 0
            ? null
            : "Bean Bot is missing these permissions in the target channel: **" +
              string.Join(", ", missing) + "**.";
    }

    private async Task<bool> AcknowledgeEphemeralComponentAsync(
        string loadingMessage,
        RequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        if (Context.Interaction is not SocketMessageComponent component
            || !IsEphemeral(component))
        {
            await RespondToInvalidComponentAsync(
                "That private role-menu control is invalid or expired.",
                requestOptions,
                cancellationToken);
            return false;
        }

        await component.UpdateAsync(
            properties => SetMessage(
                properties,
                loadingMessage,
                null,
                MessageComponent.Empty),
            requestOptions);
        return true;
    }

    private async Task RespondToInvalidComponentAsync(
        string message,
        RequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        if (Context.Interaction is SocketMessageComponent component
            && IsEphemeral(component))
        {
            await component.UpdateAsync(
                properties => SetMessage(
                    properties,
                    message,
                    null,
                    MessageComponent.Empty),
                requestOptions);
            return;
        }

        if (Context.Interaction is SocketMessageComponent publicComponent)
        {
            await publicComponent.DeferLoadingAsync(ephemeral: true, requestOptions);
            await ReplaceResponseAsync(message, cancellationToken);
            return;
        }

        await RespondAsync(
            message,
            ephemeral: true,
            allowedMentions: AllowedMentions.None,
            options: requestOptions);
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

    private static async Task<RoleMenuPanelSnapshot?> ReadPublicationPanelAsync(
        ITextChannel targetChannel,
        ulong channelId,
        ulong messageId,
        ObjectId menuId,
        CancellationToken cancellationToken)
    {
        if (targetChannel.Id != channelId)
        {
            return null;
        }

        try
        {
            var message = await targetChannel.GetMessageAsync(
                messageId,
                CacheMode.AllowDownload,
                CreateRequestOptions(cancellationToken));
            return message is IUserMessage userMessage
                ? CreatePanelSnapshot(targetChannel, userMessage, menuId)
                : null;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<RoleMenuPanelSnapshot>>
        ReadRecentPublicationPanelsAsync(
            ITextChannel targetChannel,
            ulong channelId,
            int maximumResults,
            ObjectId menuId,
            CancellationToken cancellationToken)
    {
        if (targetChannel.Id != channelId)
        {
            return [];
        }

        var messages = await targetChannel
            .GetMessagesAsync(
                maximumResults,
                CacheMode.AllowDownload,
                CreateRequestOptions(cancellationToken))
            .FlattenAsync();
        return messages
            .OfType<IUserMessage>()
            .Select(message => CreatePanelSnapshot(targetChannel, message, menuId))
            .ToList();
    }

    private static async Task<RoleMenuPanelSnapshot> SendPublicationPanelAsync(
        ITextChannel targetChannel,
        RoleMenuDraft draft,
        CancellationToken cancellationToken)
    {
        var message = await targetChannel.SendMessageAsync(
            embed: RoleMenuComponents.BuildPublicEmbed(
                draft.MenuId,
                draft.Title,
                draft.Description,
                draft.SelectionMode),
            options: CreateRequestOptions(cancellationToken),
            allowedMentions: AllowedMentions.None,
            components: RoleMenuComponents.BuildPublicComponents(draft.MenuId));
        return CreatePanelSnapshot(targetChannel, message, draft.MenuId);
    }

    private static async Task<bool> RollbackPublicationPanelAsync(
        ITextChannel targetChannel,
        RoleMenuPanelSnapshot panel,
        ObjectId menuId,
        CancellationToken cancellationToken)
    {
        if (panel.GuildId != targetChannel.GuildId
            || panel.ChannelId != targetChannel.Id
            || !panel.HasManageButton)
        {
            return false;
        }

        try
        {
            var message = await targetChannel.GetMessageAsync(
                panel.MessageId,
                CacheMode.AllowDownload,
                CreateRequestOptions(cancellationToken));
            if (message is null)
            {
                return true;
            }

            if (message.Author.Id != panel.AuthorId
                || !RoleMenuComponents.HasManageButton(message, menuId))
            {
                return false;
            }

            await message.DeleteAsync(CreateRequestOptions(cancellationToken));
            return true;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return true;
        }
    }

    private static RoleMenuPanelSnapshot CreatePanelSnapshot(
        ITextChannel channel,
        IUserMessage message,
        ObjectId menuId)
        => new(
            channel.GuildId,
            channel.Id,
            message.Id,
            message.Author.Id,
            RoleMenuComponents.HasManageButton(message, menuId));

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
        var currentAdministrator = await GetGuildUserAsync(
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
        if (result.PanelStatus is RoleMenuPanelDeletionStatus.Failed
            or RoleMenuPanelDeletionStatus.OutcomeUnknown)
        {
            BeanBotLog.RoleMenuPanelDeletionFailed(
                _logger,
                menuId.ToString(),
                result.Exception ?? new InvalidOperationException(
                    $"Panel deletion stopped with issue {result.PanelIssue}."));
        }
        else if (result.Exception is not null)
        {
            BeanBotLog.RoleMenuPersistenceDeletionFailed(
                _logger,
                menuId.ToString(),
                result.Exception);
        }

        return result;
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
                ReadDeletionPanelAsync(
                    guild,
                    expectedMenuId,
                    channelId,
                    messageId,
                    cancellationToken),
            (panel, cancellationToken) => DeleteDeletionPanelAsync(
                guild,
                menuId,
                panel,
                cancellationToken),
            (id, guildId, cancellationToken) => _roleMenuService.DeleteAsync(
                id,
                guildId,
                cancellationToken),
            () => _roleMenuService.IsShuttingDown);

    private async Task<RoleMenuPanelLookupResult> ReadDeletionPanelAsync(
        SocketGuild guild,
        ObjectId expectedMenuId,
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        var requestOptions = CreateRequestOptions(cancellationToken);
        IChannel? channel;
        try
        {
            channel = await Context.Client.Rest.GetChannelAsync(channelId, requestOptions);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.ChannelMissing);
        }

        if (channel is null)
        {
            return new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.ChannelMissing);
        }

        if (channel is not ITextChannel textChannel || textChannel.GuildId != guild.Id)
        {
            return new RoleMenuPanelLookupResult(
                RoleMenuPanelLookupStatus.UnexpectedChannelType);
        }

        IMessage? message;
        try
        {
            message = await textChannel.GetMessageAsync(
                messageId,
                CacheMode.AllowDownload,
                requestOptions);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing);
        }

        return message is null
            ? new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing)
            : new RoleMenuPanelLookupResult(
                RoleMenuPanelLookupStatus.Found,
                new RoleMenuPanelSnapshot(
                    textChannel.GuildId,
                    textChannel.Id,
                    message.Id,
                    message.Author.Id,
                    RoleMenuComponents.HasManageButton(message, expectedMenuId)));
    }

    private async Task<bool> DeleteDeletionPanelAsync(
        SocketGuild guild,
        ObjectId menuId,
        RoleMenuPanelSnapshot panel,
        CancellationToken cancellationToken)
    {
        if (panel.GuildId != guild.Id || !panel.HasManageButton)
        {
            return false;
        }

        var requestOptions = CreateRequestOptions(cancellationToken);
        try
        {
            var channel = await Context.Client.Rest.GetChannelAsync(
                panel.ChannelId,
                requestOptions);
            if (channel is null)
            {
                return true;
            }

            if (channel is not ITextChannel textChannel || textChannel.GuildId != guild.Id)
            {
                return false;
            }

            var message = await textChannel.GetMessageAsync(
                panel.MessageId,
                CacheMode.AllowDownload,
                requestOptions);
            if (message is null)
            {
                return true;
            }

            if (message.Author.Id != panel.AuthorId
                || !RoleMenuComponents.HasManageButton(message, menuId))
            {
                return false;
            }

            await message.DeleteAsync(requestOptions);
            return true;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return true;
        }
    }

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

    private static string CreateMessageUrl(
        ulong guildId,
        ulong channelId,
        ulong messageId)
        => "https://discord.com/channels/" +
           guildId.ToString(CultureInfo.InvariantCulture) + "/" +
           channelId.ToString(CultureInfo.InvariantCulture) + "/" +
           messageId.ToString(CultureInfo.InvariantCulture);
}
