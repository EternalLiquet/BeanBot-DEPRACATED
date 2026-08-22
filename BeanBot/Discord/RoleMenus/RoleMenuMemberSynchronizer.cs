using BeanBot.Persistence.Models;

namespace BeanBot.Discord.RoleMenus;

internal interface IRoleMenuMemberMutator
{
    Task AddRoleAsync(ulong roleId, CancellationToken cancellationToken);
    Task RemoveRoleAsync(ulong roleId, CancellationToken cancellationToken);
}

internal sealed record RoleMenuMutationFailure(
    ulong RoleId,
    string Action,
    Exception Exception);

internal enum RoleMenuMutationInterruptionKind
{
    NotAttempted,
    OutcomeUnknown
}

internal sealed record RoleMenuMutationInterruption(
    ulong RoleId,
    string Action,
    RoleMenuMutationInterruptionKind Kind);

internal sealed record RoleMenuSynchronizationResult(
    IReadOnlyList<ulong> AddedRoleIds,
    IReadOnlyList<ulong> RemovedRoleIds,
    IReadOnlyList<RoleMenuMutationFailure> Failures,
    IReadOnlyList<ulong> SkippedRemovalRoleIds,
    RoleMenuMutationInterruption? Interruption = null)
{
    internal bool IsComplete => Failures.Count == 0
                                && SkippedRemovalRoleIds.Count == 0
                                && Interruption is null;
}

internal sealed class RoleMenuMemberSynchronizer
{
    internal async Task<RoleMenuSynchronizationResult> SynchronizeAsync(
        RoleMenuSelectionPlan plan,
        RoleMenuSelectionMode selectionMode,
        IRoleMenuMemberMutator member,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(member);

        var added = new List<ulong>();
        var removed = new List<ulong>();
        var failures = new List<RoleMenuMutationFailure>();
        foreach (var roleId in plan.RolesToAdd)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Interrupted(
                    roleId,
                    "add",
                    RoleMenuMutationInterruptionKind.NotAttempted,
                    selectionMode == RoleMenuSelectionMode.Single
                        ? plan.RolesToRemove
                        : []);
            }

            try
            {
                await member.AddRoleAsync(roleId, cancellationToken);
                added.Add(roleId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Interrupted(
                    roleId,
                    "add",
                    RoleMenuMutationInterruptionKind.OutcomeUnknown,
                    selectionMode == RoleMenuSelectionMode.Single
                        ? plan.RolesToRemove
                        : []);
            }
            catch (Exception exception)
            {
                failures.Add(new RoleMenuMutationFailure(roleId, "add", exception));
            }
        }

        if (selectionMode == RoleMenuSelectionMode.Single
            && plan.RolesToAdd.Count > 0
            && added.Count == 0)
        {
            return new RoleMenuSynchronizationResult(
                added,
                removed,
                failures,
                plan.RolesToRemove);
        }

        foreach (var roleId in plan.RolesToRemove)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Interrupted(
                    roleId,
                    "remove",
                    RoleMenuMutationInterruptionKind.NotAttempted,
                    []);
            }

            try
            {
                await member.RemoveRoleAsync(roleId, cancellationToken);
                removed.Add(roleId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Interrupted(
                    roleId,
                    "remove",
                    RoleMenuMutationInterruptionKind.OutcomeUnknown,
                    []);
            }
            catch (Exception exception)
            {
                failures.Add(new RoleMenuMutationFailure(roleId, "remove", exception));
            }
        }

        return new RoleMenuSynchronizationResult(added, removed, failures, []);

        RoleMenuSynchronizationResult Interrupted(
            ulong roleId,
            string action,
            RoleMenuMutationInterruptionKind kind,
            IReadOnlyList<ulong> skippedRemovalRoleIds)
            => new(
                added,
                removed,
                failures,
                skippedRemovalRoleIds,
                new RoleMenuMutationInterruption(roleId, action, kind));
    }
}
