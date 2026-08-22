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
    private enum PanelDeletionStatus
    {
        DeletedOrMissing,
        UnexpectedMessage,
        Failed,
        OutcomeUnknown
    }

    private enum ConfigurationDeletionStatus
    {
        Deleted,
        AlreadyMissing,
        Kept,
        OutcomeUnknown
    }

    private sealed record PanelDeletionResult(
        PanelDeletionStatus Status,
        Exception? Exception = null);

    private sealed record MenuDeletionResult(
        ConfigurationDeletionStatus ConfigurationStatus,
        PanelDeletionStatus PanelStatus,
        bool AuthorizationDenied = false,
        Exception? Exception = null);

    private sealed record PersistenceReconciliationResult(
        bool ReadSucceeded,
        RoleMenuSettings? Settings);

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

        MenuDeletionResult result;
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
            { PanelStatus: PanelDeletionStatus.Failed } =>
                "The published panel is still present, so its saved configuration was kept. Fix " +
                "the channel permissions and retry.",
            { PanelStatus: PanelDeletionStatus.OutcomeUnknown } =>
                "Bean Bot couldn't confirm whether the published panel was deleted, so its saved " +
                "configuration was kept. Retry this command to finish cleanup safely.",
            {
                PanelStatus: PanelDeletionStatus.UnexpectedMessage,
                ConfigurationStatus: ConfigurationDeletionStatus.Kept
            } =>
                "The referenced message no longer looked like Bean Bot's panel and was left " +
                "untouched, but the saved configuration could not be deleted. Retry to finish cleanup.",
            {
                PanelStatus: PanelDeletionStatus.UnexpectedMessage,
                ConfigurationStatus: ConfigurationDeletionStatus.OutcomeUnknown
            } =>
                "The referenced message no longer looked like Bean Bot's panel and was left " +
                "untouched. Bean Bot couldn't confirm whether the saved configuration was deleted; " +
                "run this command again to check.",
            { PanelStatus: PanelDeletionStatus.UnexpectedMessage } =>
                "The saved configuration was deleted, but the referenced message no longer looked like " +
                "Bean Bot's panel and was left untouched.",
            { ConfigurationStatus: ConfigurationDeletionStatus.Kept } =>
                "The published panel is gone, but Bean Bot couldn't delete the saved configuration. " +
                "Retry this command to finish cleanup.",
            { ConfigurationStatus: ConfigurationDeletionStatus.OutcomeUnknown } =>
                "The published panel is gone, but Bean Bot couldn't confirm whether its saved " +
                "configuration was deleted. Run this command again to check.",
            { ConfigurationStatus: ConfigurationDeletionStatus.AlreadyMissing } =>
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
        var menuId = draft.MenuId;
        var requestOptions = CreateRequestOptions(cancellationToken);
        var existingSettings = await _roleMenuService.GetAsync(
            menuId,
            guildId,
            cancellationToken);
        var panel = await FindExistingPanelAsync(
            targetChannel,
            existingSettings,
            menuId,
            botUserId,
            requestOptions);
        if (panel is null)
        {
            try
            {
                panel = await targetChannel.SendMessageAsync(
                    embed: RoleMenuComponents.BuildPublicEmbed(
                        menuId,
                        draft.Title,
                        draft.Description,
                        draft.SelectionMode),
                    options: requestOptions,
                    allowedMentions: AllowedMentions.None,
                    components: RoleMenuComponents.BuildPublicComponents(menuId));
            }
            catch (Exception exception)
            {
                BeanBotLog.RoleMenuPublicationFailed(_logger, menuId.ToString(), exception);
                panel = await TryReconcilePanelAsync(
                    targetChannel,
                    existingSettings,
                    menuId,
                    botUserId);
                if (panel is null)
                {
                    _roleMenuService.CompletePublish(draft.Id, guildId, administratorId);
                    await SendFreshFeedbackAsync(
                        "Discord reported an error while publishing, and Bean Bot could not " +
                        "confirm whether a panel was created. Automatic retry was disabled to " +
                        "prevent a duplicate. Check the target channel and remove any orphaned " +
                        "panel before running `/role-menu create` again.");
                    return null;
                }
            }
        }

        var publishedPanel = panel
            ?? throw new InvalidOperationException(
                "Role-menu publication did not produce a panel to persist.");
        var settings = RoleMenuPublicationSettings.Create(
            draft,
            publishedPanel.Id,
            existingSettings?.CreatedAtUtc ?? default);
        var persistenceCommitted = false;
        try
        {
            await _roleMenuService.UpsertAsync(settings, cancellationToken);
            persistenceCommitted = true;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationFailed(_logger, menuId.ToString(), exception);
            var persistence = await TryReconcilePersistenceAsync(
                menuId,
                guildId,
                draft,
                publishedPanel.Id);
            persistenceCommitted = persistence.ReadSucceeded
                && RoleMenuPublicationSettings.Matches(
                    persistence.Settings,
                    draft,
                    publishedPanel.Id);
            if (!persistenceCommitted && persistence.ReadSucceeded
                && persistence.Settings is null)
            {
                var rollbackSucceeded = await TryRollbackPanelBoundedAsync(
                    publishedPanel,
                    menuId);
                if (rollbackSucceeded)
                {
                    await RestorePreviewFreshAsync(
                        draft,
                        roles,
                        "Bean Bot confirmed the settings were not saved and removed the panel. " +
                        "You can retry this preview safely.");
                }
                else
                {
                    _roleMenuService.CompletePublish(draft.Id, guildId, administratorId);
                    await SendFreshFeedbackAsync(
                        "Bean Bot confirmed the settings were not saved but could not remove the " +
                        "panel. Automatic retry was disabled to prevent a duplicate. Delete that " +
                        "orphaned panel manually before running `/role-menu create` again.");
                }

                return null;
            }

            if (!persistenceCommitted)
            {
                _roleMenuService.CompletePublish(draft.Id, guildId, administratorId);
                await SendFreshFeedbackAsync(
                    "Bean Bot could not confirm whether MongoDB saved this panel. The public " +
                    "panel was left in place to avoid deleting a possibly committed menu, and " +
                    "automatic retry was disabled to prevent a duplicate. Inspect the target " +
                    "channel before running `/role-menu create` again.");
                return null;
            }
        }

        _roleMenuService.CompletePublish(draft.Id, guildId, administratorId);
        return publishedPanel.Id;
    }

    private static async Task<IUserMessage?> FindExistingPanelAsync(
        ITextChannel targetChannel,
        RoleMenuSettings? existingSettings,
        ObjectId menuId,
        ulong botUserId,
        RequestOptions requestOptions)
    {
        if (existingSettings is not null
            && RoleMenuCustomIds.TryParseSnowflake(
                existingSettings.ChannelId,
                out var existingChannelId)
            && existingChannelId == targetChannel.Id
            && RoleMenuCustomIds.TryParseSnowflake(
                existingSettings.MessageId,
                out var existingMessageId))
        {
            try
            {
                var exactMessage = await targetChannel.GetMessageAsync(
                    existingMessageId,
                    CacheMode.AllowDownload,
                    requestOptions);
                if (exactMessage is IUserMessage exactPanel
                    && IsExpectedPanel(exactPanel, menuId, botUserId))
                {
                    return exactPanel;
                }
            }
            catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
            {
                // Fall through to the bounded recent-message reconciliation scan.
            }
        }

        var recentMessages = await targetChannel
            .GetMessagesAsync(
                RoleMenuConstants.PanelReconciliationSearchLimit,
                CacheMode.AllowDownload,
                requestOptions)
            .FlattenAsync();
        return recentMessages
            .OfType<IUserMessage>()
            .FirstOrDefault(message => IsExpectedPanel(message, menuId, botUserId));
    }

    private async Task<IUserMessage?> TryReconcilePanelAsync(
        ITextChannel targetChannel,
        RoleMenuSettings? existingSettings,
        ObjectId menuId,
        ulong botUserId)
    {
        using var cleanupCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        try
        {
            return await FindExistingPanelAsync(
                targetChannel,
                existingSettings,
                menuId,
                botUserId,
                CreateRequestOptions(cleanupCancellation.Token));
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPanelReconciliationFailed(
                _logger,
                menuId.ToString(),
                exception);
            return null;
        }
    }

    private async Task<PersistenceReconciliationResult> TryReconcilePersistenceAsync(
        ObjectId menuId,
        ulong guildId,
        RoleMenuDraft draft,
        ulong panelMessageId)
    {
        using var cleanupCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        try
        {
            var settings = await _roleMenuService.GetAsync(
                menuId,
                guildId,
                cleanupCancellation.Token);
            if (settings is not null
                && !RoleMenuPublicationSettings.Matches(
                    settings,
                    draft,
                    panelMessageId))
            {
                BeanBotLog.RoleMenuConfigurationInvalid(
                    _logger,
                    menuId.ToString(),
                    "persisted publication did not match the attempted configuration");
            }

            return new PersistenceReconciliationResult(true, settings);
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPersistenceReconciliationFailed(
                _logger,
                menuId.ToString(),
                exception);
            return new PersistenceReconciliationResult(false, null);
        }
    }

    private async Task<bool> TryRollbackPanelBoundedAsync(
        IUserMessage panel,
        ObjectId menuId)
    {
        using var cleanupCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        return await TryRollbackPanelAsync(
            panel,
            menuId,
            cleanupCancellation.Token);
    }

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

    private static bool IsExpectedPanel(
        IUserMessage message,
        ObjectId menuId,
        ulong botUserId)
        => message.Author.Id == botUserId
           && RoleMenuComponents.HasManageButton(message, menuId);

    private async Task<bool> TryRollbackPanelAsync(
        IUserMessage panel,
        ObjectId menuId,
        CancellationToken cancellationToken)
    {
        try
        {
            await panel.DeleteAsync(CreateRequestOptions(cancellationToken));
            return true;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationRollbackFailed(
                _logger,
                menuId.ToString(),
                exception);
            return false;
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

    private async Task<MenuDeletionResult> DeleteMenuCoreAsync(
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
        if (currentAdministrator is null
            || !currentAdministrator.GuildPermissions.ManageRoles)
        {
            return new MenuDeletionResult(
                ConfigurationDeletionStatus.Kept,
                PanelDeletionStatus.DeletedOrMissing,
                AuthorizationDenied: true);
        }

        var settings = await _roleMenuService.GetAsync(menuId, guild.Id, cancellationToken);
        if (settings is null)
        {
            return new MenuDeletionResult(
                ConfigurationDeletionStatus.AlreadyMissing,
                PanelDeletionStatus.DeletedOrMissing);
        }

        var panelResult = await DeletePanelAsync(settings, guild, bot, cancellationToken);
        if (panelResult.Status is PanelDeletionStatus.Failed
            or PanelDeletionStatus.OutcomeUnknown)
        {
            BeanBotLog.RoleMenuPanelDeletionFailed(
                _logger,
                menuId.ToString(),
                panelResult.Exception!);
            return new MenuDeletionResult(
                ConfigurationDeletionStatus.Kept,
                panelResult.Status,
                Exception: panelResult.Exception);
        }

        try
        {
            var deleted = await _roleMenuService.DeleteAsync(
                menuId,
                guild.Id,
                cancellationToken);
            return new MenuDeletionResult(
                deleted
                    ? ConfigurationDeletionStatus.Deleted
                    : ConfigurationDeletionStatus.AlreadyMissing,
                panelResult.Status);
        }
        catch (OperationCanceledException) when (_roleMenuService.IsShuttingDown)
        {
            BeanBotLog.RoleMenuDeletionInterrupted(
                _logger,
                menuId.ToString(),
                "saved configuration deletion",
                $"panel state: {panelResult.Status}; configuration outcome unknown");
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPersistenceDeletionFailed(
                _logger,
                menuId.ToString(),
                exception);
            var configurationStatus = await TryReconcileConfigurationDeletionAsync(
                menuId,
                guild.Id);
            return new MenuDeletionResult(
                configurationStatus,
                panelResult.Status,
                Exception: exception);
        }
    }

    private async Task<PanelDeletionResult> DeletePanelAsync(
        RoleMenuSettings settings,
        SocketGuild guild,
        SocketGuildUser bot,
        CancellationToken cancellationToken)
    {
        if (!RoleMenuCustomIds.TryParseSnowflake(settings.ChannelId, out var channelId)
            || !RoleMenuCustomIds.TryParseSnowflake(settings.MessageId, out var messageId))
        {
            BeanBotLog.RoleMenuConfigurationInvalid(
                _logger,
                settings.Id.ToString(),
                "invalid panel location");
            return new PanelDeletionResult(PanelDeletionStatus.UnexpectedMessage);
        }

        var requestOptions = CreateRequestOptions(cancellationToken);
        try
        {
            var channel = await Context.Client.Rest.GetChannelAsync(channelId, requestOptions);
            if (channel is null)
            {
                return new PanelDeletionResult(PanelDeletionStatus.DeletedOrMissing);
            }

            if (channel is not ITextChannel textChannel || textChannel.GuildId != guild.Id)
            {
                return new PanelDeletionResult(PanelDeletionStatus.UnexpectedMessage);
            }

            var message = await textChannel.GetMessageAsync(
                messageId,
                CacheMode.AllowDownload,
                requestOptions);
            if (message is null)
            {
                return new PanelDeletionResult(PanelDeletionStatus.DeletedOrMissing);
            }

            if (message.Author.Id != bot.Id
                || !RoleMenuComponents.HasManageButton(message, settings.Id))
            {
                BeanBotLog.RoleMenuConfigurationInvalid(
                    _logger,
                    settings.Id.ToString(),
                    "published message no longer matches the saved panel");
                return new PanelDeletionResult(PanelDeletionStatus.UnexpectedMessage);
            }

            await message.DeleteAsync(requestOptions);
            return new PanelDeletionResult(PanelDeletionStatus.DeletedOrMissing);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return new PanelDeletionResult(PanelDeletionStatus.DeletedOrMissing);
        }
        catch (OperationCanceledException) when (_roleMenuService.IsShuttingDown)
        {
            BeanBotLog.RoleMenuDeletionInterrupted(
                _logger,
                settings.Id.ToString(),
                "published panel deletion",
                "panel outcome unknown; saved configuration retained");
            throw;
        }
        catch (Exception exception)
        {
            return await TryReconcilePanelDeletionAsync(
                settings,
                guild,
                bot,
                exception);
        }
    }

    private async Task<PanelDeletionResult> TryReconcilePanelDeletionAsync(
        RoleMenuSettings settings,
        SocketGuild guild,
        SocketGuildUser bot,
        Exception deletionException)
    {
        using var cleanupCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        var requestOptions = CreateRequestOptions(cleanupCancellation.Token);
        try
        {
            if (!RoleMenuCustomIds.TryParseSnowflake(settings.ChannelId, out var channelId)
                || !RoleMenuCustomIds.TryParseSnowflake(settings.MessageId, out var messageId))
            {
                return new PanelDeletionResult(
                    PanelDeletionStatus.UnexpectedMessage,
                    deletionException);
            }

            var channel = await Context.Client.Rest.GetChannelAsync(channelId, requestOptions);
            if (channel is null)
            {
                return new PanelDeletionResult(
                    PanelDeletionStatus.DeletedOrMissing,
                    deletionException);
            }

            if (channel is not ITextChannel textChannel || textChannel.GuildId != guild.Id)
            {
                return new PanelDeletionResult(
                    PanelDeletionStatus.UnexpectedMessage,
                    deletionException);
            }

            var message = await textChannel.GetMessageAsync(
                messageId,
                CacheMode.AllowDownload,
                requestOptions);
            if (message is null)
            {
                return new PanelDeletionResult(
                    PanelDeletionStatus.DeletedOrMissing,
                    deletionException);
            }

            return message.Author.Id == bot.Id
                   && RoleMenuComponents.HasManageButton(message, settings.Id)
                ? new PanelDeletionResult(PanelDeletionStatus.Failed, deletionException)
                : new PanelDeletionResult(
                    PanelDeletionStatus.UnexpectedMessage,
                    deletionException);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return new PanelDeletionResult(
                PanelDeletionStatus.DeletedOrMissing,
                deletionException);
        }
        catch (Exception reconciliationException)
        {
            BeanBotLog.RoleMenuPanelDeletionReconciliationFailed(
                _logger,
                settings.Id.ToString(),
                reconciliationException);
            return new PanelDeletionResult(
                PanelDeletionStatus.OutcomeUnknown,
                deletionException);
        }
    }

    private async Task<ConfigurationDeletionStatus> TryReconcileConfigurationDeletionAsync(
        ObjectId menuId,
        ulong guildId)
    {
        using var cleanupCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        try
        {
            var settings = await _roleMenuService.GetAsync(
                menuId,
                guildId,
                cleanupCancellation.Token);
            return settings is null
                ? ConfigurationDeletionStatus.AlreadyMissing
                : ConfigurationDeletionStatus.Kept;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuDeletionReconciliationFailed(
                _logger,
                menuId.ToString(),
                exception);
            return ConfigurationDeletionStatus.OutcomeUnknown;
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
