using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
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

[Group("role-menu", "Create and delete role menus.")]
[CommandContextType(InteractionContextType.Guild)]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageRoles)]
[DefaultMemberPermissions(GuildPermission.ManageRoles)]
public sealed class RoleMenuAdminModule : RoleMenuModuleBase
{
    private readonly DiscordRoleMenuClient _discord;
    private readonly RoleMenuAdministrationService _administration;
    private readonly ILogger<RoleMenuAdminModule> _logger;

    public RoleMenuAdminModule(
        RoleMenuInteractionService roleMenuService,
        DiscordRoleMenuClient discord,
        RoleMenuAdministrationService administration,
        ILogger<RoleMenuAdminModule> logger)
        : base(roleMenuService)
    {
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [SlashCommand(
        "create",
        "Create a menu where members can choose their roles.",
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
                "Building your preview…",
                operationToken),
            cancellation.Token);

        if (!TryGetGuildActors(out var guild, out var administrator, out var bot))
        {
            await ReplaceResponseAsync(
                "You can only create role menus in a server.",
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
                "This preview isn't available anymore. Run `/role-menu create` again.",
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
                    "I'm already publishing this menu.",
                RoleMenuDraftAccessStatus.WrongOwner =>
                    "Only the person who made this preview can publish it.",
                _ => "This preview has expired. Run `/role-menu create` again."
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
                    "Publishing your menu…",
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
                    "I couldn't check the server's roles just now. Try again.",
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
                    "The channel you picked was deleted. Run `/role-menu create` again and pick " +
                    "another channel.",
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
                    ? "That took too long, and I couldn't confirm whether your menu was posted. " +
                      "Check the channel. Publishing again from this preview won't create a duplicate."
                    : "I was busy and didn't start publishing your menu. Try again.");
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
                "I couldn't publish your menu. Your preview is still here, so you can try again.");
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
                "This preview has expired, is already being published, or belongs to someone else.",
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
                        "Setup cancelled.",
                        null,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    "Setup cancelled.",
                    operationToken),
                cancellation.Token);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Setup cancelled.",
            cancellation.Token);
    }

    [SlashCommand(
        "delete",
        "Delete a role menu. Members keep the roles they already have.",
        runMode: RunMode.Sync)]
    public async Task DeleteAsync()
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
                "You can only delete role menus in a server.",
                cancellation.Token);
            return;
        }

        await ShowDeletePageAsync(Context.Guild, null, cancellation.Token);
    }

    [ComponentInteraction(
        RoleMenuCustomIds.DeletePagePattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ChangeDeletePageAsync(
        string userIdValue,
        string directionValue,
        string createdAtTicksValue,
        string menuIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !RoleMenuCustomIds.TryParsePageCursor(
                directionValue,
                createdAtTicksValue,
                menuIdValue,
                out var cursor)
            || Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(
                component,
                Context.Guild,
                ComponentType.Button,
                RoleMenuCustomIds.DeletePage(boundUserId, cursor)))
        {
            await RespondToInvalidComponentAsync(
                "This list has expired or belongs to someone else. Run `/role-menu delete` again.",
                cancellation.Token);
            return;
        }

        if (!await AcknowledgeEphemeralComponentAsync(
                "Loading role menus…",
                cancellation.Token))
        {
            return;
        }

        await ShowDeletePageAsync(Context.Guild, cursor, cancellation.Token);
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
            || !IsValidManagementComponent(
                component,
                Context.Guild,
                ComponentType.SelectMenu,
                RoleMenuCustomIds.DeleteSelect(boundUserId),
                selectedMenuIds[0]))
        {
            await RespondToInvalidComponentAsync(
                "This list has expired or belongs to someone else. Run `/role-menu delete` again.",
                cancellation.Token);
            return;
        }

        if (!await AcknowledgeEphemeralComponentAsync(
                "Loading that menu…",
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
                "That role menu was already deleted.",
                cancellation.Token);
            return;
        }

        var panelState = await _administration.InspectPanelAsync(
            settings,
            Context.Guild.Id,
            Context.Guild.CurrentUser.Id,
            cancellation.Token);
        await ReplaceResponseAsync(
            RoleMenuComponents.FormatDeleteConfirmationContent(panelState),
            cancellation.Token,
            RoleMenuComponents.BuildDeleteConfirmationEmbed(settings, panelState),
            RoleMenuComponents.BuildDeleteConfirmationComponents(
                Context.User.Id,
                settings,
                panelState));
    }

    [ComponentInteraction(
        RoleMenuCustomIds.DeleteConfirmPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ConfirmDeleteAsync(
        string userIdValue,
        string menuIdValue,
        string menuVersionValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !RoleMenuCustomIds.TryParseMenuId(menuIdValue, out var menuId)
            || !RoleMenuCustomIds.TryParseMenuVersion(menuVersionValue, out var menuVersion)
            || !TryGetGuildActors(out var guild, out _, out var bot)
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(
                component,
                guild,
                ComponentType.Button,
                RoleMenuCustomIds.DeleteConfirm(boundUserId, menuId, menuVersion)))
        {
            await RespondToInvalidComponentAsync(
                "This confirmation has expired or belongs to someone else. Run `/role-menu delete` again.",
                cancellation.Token);
            return;
        }

        if (!RoleMenus.TryBeginDeletion(menuId, boundUserId))
        {
            // A repeated click while the first one is still working; leave its progress showing.
            await RoleMenus.ExecuteInitialResponseAsync(
                supportsOriginalResponse: false,
                operationToken => component.DeferAsync(
                    ephemeral: true,
                    CreateRequestOptions(operationToken)),
                _ => Task.CompletedTask,
                cancellation.Token);
            return;
        }

        try
        {
            await ConfirmDeleteClaimedAsync(guild, bot, menuId, menuVersion, cancellation);
        }
        finally
        {
            RoleMenus.EndDeletion(menuId, boundUserId);
        }
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
                "This confirmation belongs to someone else.",
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
                        "Deletion cancelled.",
                        null,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    "Deletion cancelled.",
                    operationToken),
                cancellation.Token);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Deletion cancelled.",
            cancellation.Token);
    }

    private async Task ConfirmDeleteClaimedAsync(
        SocketGuild guild,
        SocketGuildUser bot,
        ObjectId menuId,
        long menuVersion,
        CancellationTokenSource cancellation)
    {
        if (!await AcknowledgeEphemeralComponentAsync(
                "Deleting the menu…",
                cancellation.Token))
        {
            return;
        }

        RoleMenuConfirmedDeletion deletion;
        var mutationStarted = false;
        try
        {
            deletion = await RoleMenus.RunMenuMutationAsync(
                menuId,
                operationToken =>
                {
                    mutationStarted = true;
                    return _administration.DeleteConfirmedAsync(
                        menuId,
                        menuVersion,
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
                    ? "That took too long, and I couldn't confirm whether the menu was deleted. Run " +
                      "`/role-menu delete` again to check and finish."
                    : "I was busy and didn't start deleting the menu. Try again.");
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
                "I couldn't confirm whether the menu was deleted. Run `/role-menu delete` again to " +
                "check and finish.");
            return;
        }

        await SendFreshFeedbackAsync(FormatConfirmedDeletion(deletion));
    }

    private async Task ShowDeletePageAsync(
        SocketGuild guild,
        RoleMenuPageCursor? cursor,
        CancellationToken cancellationToken)
    {
        var page = await _administration.LoadDeletionPageAsync(
            guild.Id,
            cursor,
            cancellationToken);
        if (page.Menus.Count == 0)
        {
            await ReplaceResponseAsync(
                "There are no role menus in this server yet.",
                cancellationToken);
            return;
        }

        await ReplaceResponseAsync(
            "Which role menu do you want to delete?",
            cancellationToken,
            components: RoleMenuComponents.BuildDeleteSelector(
                Context.User.Id,
                page,
                channelId => guild.GetChannel(channelId)?.Name));
    }

    private static bool IsValidManagementComponent(
        SocketMessageComponent component,
        SocketGuild guild,
        ComponentType expectedType,
        string expectedCustomId,
        string? selectedValue = null)
        => !RoleMenuDeletionTargets.IsControlExpired(
               component.Message.CreatedAt,
               DateTimeOffset.UtcNow)
           && IsValidPrivateComponent(
               component,
               guild,
               expectedType,
               expectedCustomId,
               selectedValue);

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
                "I couldn't save your menu, so I took the message back down. You can publish again " +
                "from this preview.");
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
        const string content = "Your role menu is ready.";
        var viewMenuLink = RoleMenuComponents.BuildViewMenuLink(
            CreateMessageUrl(guildId, channelId, messageId));
        if (!operationCancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReplaceResponseAsync(
                    content,
                    operationCancellationToken,
                    components: viewMenuLink);
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
            await ReplaceResponseAsync(
                content,
                feedbackCancellation.Token,
                components: viewMenuLink);
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationConfirmationFailed(
                _logger,
                menuId.ToString(),
                exception);
        }
    }

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
