using System.Globalization;
using BeanBot.Discord.Interactions;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

public sealed class RoleMenuInteractionService
{
    private readonly RoleMenuRepository _repository;
    private readonly RoleMenuDraftRegistry _draftRegistry;
    private readonly RoleMenuMutationCoordinator _mutationCoordinator;
    private readonly InteractionExecutionContext _executionContext;

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

    internal CancellationTokenSource CreateFeedbackCancellation()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _executionContext.CancellationToken);
        cancellation.CancelAfter(RoleMenuConstants.InteractionFeedbackTimeout);
        return cancellation;
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

    internal Task<bool> DeleteAsync(
        ObjectId id,
        ulong guildId,
        CancellationToken cancellationToken)
        => _repository.DeleteAsync(
            id,
            guildId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    internal Task<RoleMenuSynchronizationResult> SynchronizeAsync(
        RoleMenuSelectionPlan plan,
        RoleMenuSelectionMode selectionMode,
        IRoleMenuMemberMutator member,
        CancellationToken cancellationToken)
        => RoleMenuMemberSynchronizer.SynchronizeAsync(
            plan,
            selectionMode,
            member,
            cancellationToken);

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
