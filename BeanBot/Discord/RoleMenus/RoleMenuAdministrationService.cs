using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
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
            return new RoleMenuPreviewResult("Bean Bot couldn't refresh the current server role hierarchy. Try again in a moment.");
        }

        var title = request.Title.Trim();
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
            return new RoleMenuPreviewResult("That target channel no longer exists in this server.");
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
                    ? "Your previous role menu is still publishing. Wait for it to finish before " +
                      "starting another preview."
                    : "Bean Bot is already holding the maximum number of role-menu previews. " +
                      "Try again after another preview expires.");
        }

        return new RoleMenuPreviewResult(
            "Review this private preview, then publish it when it looks right.", draft, roleValidation.Roles);
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

internal sealed record RoleMenuPreviewResult(
    string Content,
    RoleMenuDraft? Draft = null,
    IReadOnlyCollection<RoleMenuRoleSnapshot>? Roles = null);
