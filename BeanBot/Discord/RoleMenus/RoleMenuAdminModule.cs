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
public sealed class RoleMenuAdminModule : RoleMenuModuleBase
{
    private readonly DiscordRoleMenuClient _discord;
    private readonly RoleMenuAdministrationService _administration;
    private readonly RoleMenuAuditService _audit;
    private readonly ILogger<RoleMenuAdminModule> _logger;

    public RoleMenuAdminModule(
        RoleMenuInteractionService roleMenuService,
        DiscordRoleMenuClient discord,
        RoleMenuAdministrationService administration,
        RoleMenuAuditService audit,
        ILogger<RoleMenuAdminModule> logger)
        : base(roleMenuService)
    {
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [SlashCommand(
        "create",
        "Create a native dropdown panel for self-assignable roles.",
        runMode: RunMode.Sync)]
    public async Task CreateAsync()
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
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
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
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

        var preview = await _administration.CreatePreviewAsync(
            new RoleMenuCreateRequest(
                modal.PanelTitle, modal.Description, modal.SelectionMode,
                modal.TargetChannel?.Id, modal.TargetChannel?.GuildId, modal.TargetChannel?.ChannelType,
                modal.Roles?.Select(role => role.Id).ToArray()),
            guild.Id, administrator.Id, bot.Id, cancellation.Token);
        await ReplaceResponseAsync(
            preview.Content,
            cancellation.Token,
            preview.Draft is null ? null : RoleMenuComponents.BuildPreviewEmbed(preview.Draft, preview.Roles ?? []),
            preview.Draft is null ? MessageComponent.Empty : RoleMenuComponents.BuildPreviewComponents(preview.Draft.Id));
    }

    [ComponentInteraction(
        RoleMenuCustomIds.PublishPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task PublishAsync(string draftIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
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

        var accessStatus = RoleMenus.TryBeginPublish(
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

            var publishedMessageId = await RoleMenus.RunMenuMutationAsync(
                draft.MenuId,
                operationToken =>
                {
                    publicationStarted = true;
                    return PublishMenuUnderLockAsync(
                        draft,
                        validation.Roles,
                        targetChannel,
                        currentBot.Id,
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
            when (cancellation.IsCancellationRequested && !RoleMenus.IsShuttingDown)
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
            RoleMenus.ReleasePublish(draft.Id, guild.Id, administrator.Id);
        }
    }

    [ComponentInteraction(
        RoleMenuCustomIds.CancelPublishPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task CancelPublishAsync(string draftIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseDraftId(draftIdValue, out var draftId)
            || Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidPrivateComponent(
                component,
                Context.Guild,
                ComponentType.Button,
                RoleMenuCustomIds.CancelPublish(draftId))
            || !RoleMenus.CancelDraft(draftId, Context.Guild.Id, Context.User.Id))
        {
            await RespondToInvalidComponentAsync(
                "That preview expired, belongs to another administrator, or is already publishing.",
                cancellation.Token);
            return;
        }

        if (Context.Interaction is SocketMessageComponent validComponent)
        {
            await RoleMenus.ExecuteInitialResponseAsync(
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
        "audit",
        "Audit saved role menus for stale panels, roles, or permissions.",
        runMode: RunMode.Sync)]
    public async Task AuditAsync(
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
                "Auditing role menus…",
                operationToken),
            cancellation.Token);
        if (Context.Guild is null)
        {
            await ReplaceResponseAsync(
                "Role menus can only be audited inside a server.",
                cancellation.Token);
            return;
        }

        ObjectId? requestedMenuId = null;
        if (!string.IsNullOrWhiteSpace(menuId))
        {
            if (!RoleMenuCustomIds.TryParseMenuId(menuId.Trim(), out var parsedMenuId))
            {
                await ReplaceResponseAsync(
                    "That menu ID is invalid. Copy the ID from the role panel footer.",
                    cancellation.Token);
                return;
            }

            requestedMenuId = parsedMenuId;
        }

        var result = await _audit.AuditAsync(
            Context.Guild.Id,
            Context.Guild.CurrentUser.Id,
            requestedMenuId,
            cancellation.Token);
        await ReplaceResponseAsync(
            RoleMenuAuditPresentation.Format(result, requestedMenuId),
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

            await ShowDeleteConfirmationAsync(settings, cancellation.Token);
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

        var settings = await RoleMenus.GetAsync(
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
        using var cancellation = RoleMenus.CreateOperationCancellation();
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
            result = await RoleMenus.RunMenuMutationAsync(
                menuId,
                operationToken =>
                {
                    mutationStarted = true;
                    return _administration.DeleteAsync(
                        menuId,
                        guild.Id,
                        bot.Id,
                        Context.User.Id,
                        operationToken);
                },
                cancellation.Token);
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !RoleMenus.IsShuttingDown)
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

        await SendFreshFeedbackAsync(FormatDeletion(result));
    }

    [ComponentInteraction(
        RoleMenuCustomIds.DeleteCancelPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task CancelDeleteAsync(string userIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
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
            await RoleMenus.ExecuteInitialResponseAsync(
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
        if (RoleMenus.IsShuttingDown)
        {
            throw new OperationCanceledException();
        }

        using var feedbackCancellation = RoleMenus.CreateFeedbackCancellation();
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
        CancellationToken cancellationToken)
    {
        var result = await _administration.PublishAsync(draft, targetChannel, botUserId, cancellationToken);
        if (result.Status == RoleMenuPublicationStatus.Published)
        {
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

        BeanBotLog.RoleMenuConfigurationInvalid(
            _logger,
            draft.MenuId.ToString(),
            $"publication ended in terminal state {result.Status}");
        await SendTerminalPublicationFeedbackAsync(draft.MenuId, FormatTerminalPublication(result.Status));
        return null;
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

        if (RoleMenus.IsShuttingDown)
        {
            return;
        }

        using var feedbackCancellation = RoleMenus.CreateFeedbackCancellation();
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

    private async Task SendTerminalPublicationFeedbackAsync(
        ObjectId menuId,
        string content)
    {
        try
        {
            await SendFreshFeedbackAsync(content);
        }
        catch (OperationCanceledException) when (RoleMenus.IsShuttingDown)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationFailed(_logger, menuId.ToString(), exception);
        }
    }

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
