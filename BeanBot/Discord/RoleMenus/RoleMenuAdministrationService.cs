using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuPresentation;
using static BeanBot.Discord.RoleMenus.RoleMenuSetupValidation;

namespace BeanBot.Discord.RoleMenus;

public sealed class RoleMenuAdministrationService
{
    private readonly RoleMenuInteractionService _roleMenuService;
    private readonly DiscordRoleMenuClient _discord;
    private readonly ILogger<RoleMenuAdministrationService> _logger;

    public RoleMenuAdministrationService(
        RoleMenuInteractionService roleMenuService,
        DiscordRoleMenuClient discord,
        ILogger<RoleMenuAdministrationService> logger)
    {
        _roleMenuService = roleMenuService ?? throw new ArgumentNullException(nameof(roleMenuService));
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal async Task<RoleMenuPreviewResult> CreatePreviewAsync(
        RoleMenuCreateRequest request,
        ulong guildId,
        ulong administratorId,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        var requestOptions = CreateRequestOptions(cancellationToken);
        var currentAdministrator = await _discord.GetGuildUserAsync(
            guildId,
            administratorId,
            requestOptions);
        var currentBot = await _discord.GetGuildUserAsync(guildId, botUserId, requestOptions);
        if (currentAdministrator is null || currentBot is null)
        {
            return new RoleMenuPreviewResult("I couldn't check the server's roles just now. Try again in a moment.");
        }

        var title = request.Title?.Trim() ?? string.Empty;
        var description = request.Description?.Trim() ?? string.Empty;
        if (!TryParseAndValidateModal(
                request,
                guildId,
                currentAdministrator,
                currentBot,
                title,
                description,
                out var targetChannelId,
                out var selectionMode,
                out var roleValidation,
                out var validationMessage))
        {
            return new RoleMenuPreviewResult(validationMessage);
        }

        var targetChannel = await _discord.GetGuildTextChannelAsync(
            guildId,
            targetChannelId,
            requestOptions);
        if (targetChannel is null)
        {
            return new RoleMenuPreviewResult("The channel you picked was deleted. Run `/role-menu create` again and pick another channel.");
        }

        var channelPermissionFailure = GetChannelPermissionFailure(currentBot, targetChannel);
        if (channelPermissionFailure is not null)
        {
            return new RoleMenuPreviewResult(channelPermissionFailure);
        }

        var createStatus = _roleMenuService.CreateDraft(
            guildId,
            administratorId,
            targetChannel.Id,
            title,
            description,
            roleValidation.Roles.Select(role => role.Id).ToList(),
            selectionMode,
            out var draft);
        if (createStatus != RoleMenuDraftCreateStatus.Created || draft is null)
        {
            return new RoleMenuPreviewResult(createStatus == RoleMenuDraftCreateStatus.AlreadyPublishing
                    ? "Your last menu is still being published. Wait for it to finish, then try again."
                    : "Too many role menu previews are open right now. Try again in a few minutes.");
        }

        return new RoleMenuPreviewResult(
            "Here's a preview of your menu. It isn't posted yet.", draft, roleValidation.Roles);
    }

    internal async Task<RoleMenuPublicationResult> PublishAsync(
        RoleMenuDraft draft,
        ITextChannel targetChannel,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        var result = await RoleMenuPublicationWorkflow.ExecuteAsync(
            draft, botUserId, CreatePublicationOperations(targetChannel, draft.MenuId), cancellationToken);
        LogPublicationFailures(draft.MenuId, result.Failures);
        if (result.IsTerminal)
        {
            _roleMenuService.CompletePublish(draft.Id, draft.GuildId, draft.UserId);
        }

        return result;
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

    internal async Task<RoleMenuDeletionResult> DeleteAsync(
        ObjectId menuId,
        ulong guildId,
        ulong botUserId,
        ulong administratorId,
        CancellationToken cancellationToken)
    {
        var currentAdministrator = await _discord.GetGuildUserAsync(
            guildId,
            administratorId,
            CreateRequestOptions(cancellationToken));
        var result = await RoleMenuDeletionWorkflow.ExecuteAsync(
            menuId,
            guildId,
            botUserId,
            currentAdministrator?.GuildPermissions.ManageRoles == true,
            CreateDeletionOperations(guildId, menuId),
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

    /// <summary>
    /// Deletes the exact menu version an administrator confirmed. If the menu is gone or changed
    /// since the confirmation was shown, nothing is touched.
    /// </summary>
    internal async Task<RoleMenuConfirmedDeletion> DeleteConfirmedAsync(
        ObjectId menuId,
        long confirmedVersion,
        ulong guildId,
        ulong botUserId,
        ulong administratorId,
        CancellationToken cancellationToken)
    {
        var current = await _roleMenuService.GetAsync(menuId, guildId, cancellationToken);
        if (current is null)
        {
            return new RoleMenuConfirmedDeletion(RoleMenuConfirmedDeletionStatus.AlreadyDeleted);
        }

        if (RoleMenuDeletionTargets.GetVersion(current) != confirmedVersion)
        {
            return new RoleMenuConfirmedDeletion(RoleMenuConfirmedDeletionStatus.Changed);
        }

        var result = await DeleteAsync(
            menuId,
            guildId,
            botUserId,
            administratorId,
            cancellationToken);
        return new RoleMenuConfirmedDeletion(RoleMenuConfirmedDeletionStatus.Attempted, result);
    }

    internal async Task<RoleMenuDeletionPage> LoadDeletionPageAsync(
        ulong guildId,
        RoleMenuPageCursor? cursor,
        CancellationToken cancellationToken)
    {
        var fetched = await _roleMenuService.GetPageAsync(
            guildId,
            cursor,
            RoleMenuConstants.MaximumListedMenus + 1,
            cancellationToken);
        if (fetched.Count == 0 && cursor is not null)
        {
            // Every menu past this page edge was deleted meanwhile, so start over from the newest.
            cursor = null;
            fetched = await _roleMenuService.GetPageAsync(
                guildId,
                cursor,
                RoleMenuConstants.MaximumListedMenus + 1,
                cancellationToken);
        }

        return RoleMenuDeletionTargets.BuildPage(
            fetched,
            cursor,
            RoleMenuConstants.MaximumListedMenus);
    }

    internal async Task<RoleMenuSettings?> FindMenuForMessageAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        IReadOnlyCollection<ObjectId> manageButtonMenuIds,
        CancellationToken cancellationToken)
    {
        var savedMenus = await _roleMenuService.GetByMessageAsync(
            guildId,
            channelId,
            messageId,
            RoleMenuDeletionTargets.MaximumMessageMatches,
            cancellationToken);
        return RoleMenuDeletionTargets.MatchSavedMenu(
            savedMenus,
            manageButtonMenuIds,
            guildId,
            channelId,
            messageId);
    }

    internal async Task<RoleMenuPanelState> InspectPanelAsync(
        RoleMenuSettings settings,
        ulong guildId,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!RoleMenuCustomIds.TryParseSnowflake(settings.ChannelId, out var channelId)
            || !RoleMenuCustomIds.TryParseSnowflake(settings.MessageId, out var messageId))
        {
            return RoleMenuPanelState.NotAPanel;
        }

        try
        {
            var lookup = await _discord.ReadDeletionPanelAsync(
                guildId,
                settings.Id,
                channelId,
                messageId,
                cancellationToken);
            return RoleMenuDeletionTargets.ClassifyPanel(lookup, settings, guildId, botUserId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var state = RoleMenuDeletionTargets.ClassifyPanelFailure(exception);
            if (state == RoleMenuPanelState.Unavailable)
            {
                BeanBotLog.RoleMenuPanelInspectionFailed(_logger, settings.Id.ToString(), exception);
            }

            return state;
        }
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
        ulong guildId,
        ObjectId menuId)
        => new(
            (id, guildId, cancellationToken) => _roleMenuService.GetAsync(
                id,
                guildId,
                cancellationToken),
            (expectedMenuId, channelId, messageId, cancellationToken) =>
                _discord.ReadDeletionPanelAsync(
                    guildId,
                    expectedMenuId,
                    channelId,
                    messageId,
                    cancellationToken),
            (panel, cancellationToken) => _discord.DeleteDeletionPanelAsync(
                    guildId,
                menuId,
                panel,
                cancellationToken),
            (id, guildId, cancellationToken) => _roleMenuService.DeleteAsync(
                id,
                guildId,
                cancellationToken),
            () => _roleMenuService.IsShuttingDown);

}

internal sealed record RoleMenuCreateRequest(
    string Title,
    string? Description,
    string SelectionMode,
    ulong? TargetChannelId,
    ulong? TargetChannelGuildId,
    ChannelType? TargetChannelType,
    IReadOnlyCollection<ulong>? RoleIds);

internal enum RoleMenuConfirmedDeletionStatus
{
    Attempted,
    AlreadyDeleted,
    Changed
}

internal sealed record RoleMenuConfirmedDeletion(
    RoleMenuConfirmedDeletionStatus Status,
    RoleMenuDeletionResult? Result = null);

internal sealed record RoleMenuPreviewResult(
    string Content,
    RoleMenuDraft? Draft = null,
    IReadOnlyCollection<RoleMenuRoleSnapshot>? Roles = null);
