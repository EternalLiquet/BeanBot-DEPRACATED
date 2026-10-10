using System.Collections.Concurrent;
using System.Globalization;
using BeanBot.Discord.Interactions;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

public sealed class RoleMenuInteractionService
{
    private readonly RoleMenuRepository _repository;
    private readonly RoleMenuDraftRegistry _draftRegistry;
    private readonly RoleMenuMutationCoordinator _mutationCoordinator;
    private readonly InteractionExecutionContext _executionContext;
    private readonly ConcurrentDictionary<(ObjectId MenuId, ulong UserId), byte> _deletionsInProgress = new();

    internal RoleMenuInteractionService(
        RoleMenuRepository repository,
        RoleMenuDraftRegistry draftRegistry,
        RoleMenuMutationCoordinator mutationCoordinator,
        InteractionExecutionContext executionContext)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _draftRegistry = draftRegistry ?? throw new ArgumentNullException(nameof(draftRegistry));
        _mutationCoordinator = mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
        _executionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
    }

    internal CancellationTokenSource CreateOperationCancellation()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _executionContext.CancellationToken);
        cancellation.CancelAfter(RoleMenuConstants.InteractionOperationTimeout);
        return cancellation;
    }

    internal bool IsShuttingDown
        => _executionContext.CancellationToken.IsCancellationRequested;

    internal bool CreateMigrationSelection(
        ulong guildId, ulong userId, ulong? targetChannelId,
        ulong? targetChannelGuildId, ChannelType? targetChannelType,
        string? title, string? description,
        out RoleMenuMigrationSelection? selection)
        => _draftRegistry.CreateMigrationSelection(
            guildId, userId, targetChannelId, targetChannelGuildId,
            targetChannelType, title, description, out selection);

    internal bool TryGetMigrationSelection(
        Guid id, ulong guildId, ulong userId,
        out RoleMenuMigrationSelection? selection)
        => _draftRegistry.TryGetMigrationSelection(id, guildId, userId, out selection);

    internal CancellationTokenSource CreateFeedbackCancellation()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _executionContext.CancellationToken);
        cancellation.CancelAfter(RoleMenuConstants.InteractionFeedbackTimeout);
        return cancellation;
    }

    internal async Task ExecuteInitialResponseAsync(
        bool supportsOriginalResponse,
        Func<CancellationToken, Task> sendInitial,
        Func<CancellationToken, Task> reconcileOriginal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sendInitial);
        ArgumentNullException.ThrowIfNull(reconcileOriginal);
        using var initialResponseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        initialResponseCancellation.CancelAfter(RoleMenuConstants.InteractionFeedbackTimeout);
        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            _executionContext,
            supportsOriginalResponse,
            RoleMenuConstants.InteractionFeedbackTimeout,
            new InteractionInitialResponseOperations(
                sendInitial,
                reconcileOriginal,
                InteractionResponseErrors.IsKnownMissingOriginal),
            initialResponseCancellation.Token);
        result.ThrowIfUnconfirmed();
    }

    internal RoleMenuDraftCreateStatus CreateDraft(
        ulong guildId,
        ulong userId,
        ulong targetChannelId,
        string title,
        string description,
        IReadOnlyCollection<ulong> roleIds,
        RoleMenuSelectionMode selectionMode,
        out RoleMenuDraft? draft)
        => _draftRegistry.Create(
            guildId,
            userId,
            targetChannelId,
            title,
            description,
            roleIds,
            selectionMode,
            out draft);

    internal RoleMenuDraftCreateStatus CreateMigrationDraft(
        ulong guildId,
        ulong userId,
        ulong targetChannelId,
        string title,
        string description,
        IReadOnlyCollection<ulong> roleIds,
        ObjectId menuId,
        ulong legacyReactionRoleMessageId,
        string legacySourceFingerprint,
        out RoleMenuDraft? draft)
        => _draftRegistry.CreateMigration(
            guildId,
            userId,
            targetChannelId,
            title,
            description,
            roleIds,
            menuId,
            legacyReactionRoleMessageId,
            legacySourceFingerprint,
            out draft);

    internal RoleMenuDraftAccessStatus TryBeginPublish(
        Guid draftId,
        ulong guildId,
        ulong userId,
        out RoleMenuDraft? draft)
        => _draftRegistry.TryBeginPublish(draftId, guildId, userId, out draft);

    internal bool CancelDraft(Guid draftId, ulong guildId, ulong userId)
        => _draftRegistry.Cancel(draftId, guildId, userId);

    internal void ReleasePublish(Guid draftId, ulong guildId, ulong userId)
        => _draftRegistry.ReleasePublish(draftId, guildId, userId);

    internal void CompletePublish(Guid draftId, ulong guildId, ulong userId)
        => _draftRegistry.CompletePublish(draftId, guildId, userId);

    internal RoleMenuEditDraftCreateStatus CreateEditDraft(
        ObjectId menuId,
        ulong guildId,
        ulong userId,
        string title,
        string description,
        IReadOnlyCollection<ulong> roleIds,
        RoleMenuSelectionMode selectionMode,
        out RoleMenuEditDraft? draft,
        RoleMenuEditSnapshot? snapshot = null)
        => _draftRegistry.CreateEdit(
            menuId,
            guildId,
            userId,
            title,
            description,
            roleIds,
            selectionMode,
            out draft,
            snapshot);

    internal RoleMenuEditDraftAccessStatus TryGetEditDraft(
        Guid draftId,
        ulong guildId,
        ulong userId,
        out RoleMenuEditDraft? draft)
        => _draftRegistry.TryGetEdit(draftId, guildId, userId, out draft);

    internal RoleMenuEditDraftAccessStatus TryBeginEdit(
        Guid draftId,
        ulong guildId,
        ulong userId,
        out RoleMenuEditDraft? draft)
        => _draftRegistry.TryBeginEdit(draftId, guildId, userId, out draft);

    internal void ReleaseEdit(Guid draftId, ulong guildId, ulong userId)
        => _draftRegistry.ReleaseEdit(draftId, guildId, userId);

    internal void CompleteEdit(Guid draftId, ulong guildId, ulong userId)
        => _draftRegistry.CompleteEdit(draftId, guildId, userId);

    internal Task UpsertAsync(
        RoleMenuSettings settings,
        CancellationToken cancellationToken)
        => _repository.UpsertAsync(settings, cancellationToken);

    internal Task<RoleMenuSettings?> GetAsync(
        ObjectId id,
        ulong guildId,
        CancellationToken cancellationToken)
        => _repository.GetAsync(
            id,
            guildId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    internal Task<List<RoleMenuSettings>> GetByGuildAsync(
        ulong guildId,
        int maximumResults,
        CancellationToken cancellationToken)
        => _repository.GetByGuildAsync(
            guildId.ToString(CultureInfo.InvariantCulture),
            maximumResults,
            cancellationToken);

    internal Task<List<RoleMenuSettings>> GetByMessageAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        int maximumResults,
        CancellationToken cancellationToken)
        => _repository.GetByMessageAsync(
            guildId.ToString(CultureInfo.InvariantCulture),
            channelId.ToString(CultureInfo.InvariantCulture),
            messageId.ToString(CultureInfo.InvariantCulture),
            maximumResults,
            cancellationToken);

    internal Task<List<RoleMenuSettings>> GetPageAsync(
        ulong guildId,
        RoleMenuPageCursor? cursor,
        int maximumResults,
        CancellationToken cancellationToken)
        => _repository.GetPageAsync(
            guildId.ToString(CultureInfo.InvariantCulture),
            cursor,
            maximumResults,
            cancellationToken);

    internal Task<bool> DeleteAsync(
        ObjectId id,
        ulong guildId,
        CancellationToken cancellationToken)
        => _repository.DeleteAsync(
            id,
            guildId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    internal async Task<int> DeleteSavedPanelsForMessageAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        const int maximumMatches = 25;
        var guild = guildId.ToString(CultureInfo.InvariantCulture);
        var channel = channelId.ToString(CultureInfo.InvariantCulture);
        var message = messageId.ToString(CultureInfo.InvariantCulture);
        var matches = await _repository.GetByMessageAsync(
            guild, channel, message, maximumMatches + 1, cancellationToken);
        if (matches.Count > maximumMatches)
        {
            throw new InvalidOperationException("Too many saved role menus match a deleted message.");
        }
        var deleted = 0;
        foreach (var settings in matches)
        {
            if (await RunMenuMutationAsync(
                    settings.Id,
                    async token =>
                    {
                        // The initial message lookup is only a bounded candidate list. An edit
                        // may replace that revision before this lock is acquired.
                        var current = await _repository.GetAsync(settings.Id, guild, token);
                        return current is not null
                            && await _repository.DeleteBindingAsync(
                                current, guild, channel, message, token);
                    },
                    cancellationToken))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Claims one administrator's confirmed deletion of a menu so a repeated click can't start a
    /// second one. Each claim belongs to a live interaction and is released when it finishes.
    /// Different administrators are still serialized by the menu lock.
    /// </summary>
    internal bool TryBeginDeletion(ObjectId menuId, ulong userId)
        => _deletionsInProgress.TryAdd((menuId, userId), 0);

    internal void EndDeletion(ObjectId menuId, ulong userId)
        => _deletionsInProgress.TryRemove((menuId, userId), out _);

    internal Task<T> RunMenuMutationAsync<T>(
        ObjectId menuId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        => _mutationCoordinator.RunMenuWriteAsync(
            $"menu:{menuId}",
            operation,
            cancellationToken);

    internal Task<T> RunMemberMutationAsync<T>(
        ObjectId menuId,
        ulong guildId,
        ulong memberId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        => _mutationCoordinator.RunMemberAsync(
            $"menu:{menuId}",
            $"member:{guildId.ToString(CultureInfo.InvariantCulture)}:" +
            memberId.ToString(CultureInfo.InvariantCulture),
            operation,
            cancellationToken);
}
