using BeanBot.Persistence.Models;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuPanelUpdateStatus
{
    Updated,
    Missing,
    UnexpectedMessage
}

internal enum RoleMenuEditCommitStatus
{
    Updated,
    PersistenceOutcomeUnknown,
    PanelMissing,
    PanelUnexpected,
    PanelOutcomeUnknown
}

internal enum RoleMenuEditFailurePhase
{
    Persistence,
    PanelUpdate
}

internal sealed record RoleMenuEditFailure(
    RoleMenuEditFailurePhase Phase,
    Exception Exception);

internal sealed record RoleMenuEditCommitResult(
    RoleMenuEditCommitStatus Status,
    IReadOnlyList<RoleMenuEditFailure>? FailureList = null)
{
    internal IReadOnlyList<RoleMenuEditFailure> Failures { get; } = FailureList ?? [];
}

internal sealed record RoleMenuEditCommitOperations(
    Func<RoleMenuSettings, CancellationToken, Task> PersistSettings,
    Func<RoleMenuSettings, CancellationToken, Task<RoleMenuPanelUpdateStatus>> UpdatePanel,
    Func<bool> IsShuttingDown);

internal static class RoleMenuEditWorkflow
{
    internal static async Task<RoleMenuEditCommitResult> ExecuteAsync(
        RoleMenuSettings replacement,
        RoleMenuEditCommitOperations operations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(operations.PersistSettings);
        ArgumentNullException.ThrowIfNull(operations.UpdatePanel);
        ArgumentNullException.ThrowIfNull(operations.IsShuttingDown);

        try
        {
            await operations.PersistSettings(replacement, cancellationToken);
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception exception)
        {
            // The repository write may have reached MongoDB before an error became visible.
            // Presentation therefore remains untouched and the administrator must re-run
            // the edit to reconcile from persisted truth rather than guessing or rolling back.
            return new RoleMenuEditCommitResult(
                RoleMenuEditCommitStatus.PersistenceOutcomeUnknown,
                [new RoleMenuEditFailure(RoleMenuEditFailurePhase.Persistence, exception)]);
        }

        try
        {
            var panelStatus = await operations.UpdatePanel(replacement, cancellationToken);
            return new RoleMenuEditCommitResult(panelStatus switch
            {
                RoleMenuPanelUpdateStatus.Updated => RoleMenuEditCommitStatus.Updated,
                RoleMenuPanelUpdateStatus.Missing => RoleMenuEditCommitStatus.PanelMissing,
                RoleMenuPanelUpdateStatus.UnexpectedMessage => RoleMenuEditCommitStatus.PanelUnexpected,
                _ => throw new InvalidOperationException("Unknown role-menu panel update status.")
            });
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception exception)
        {
            // Persisted settings are authoritative. Do not retry the Discord mutation here:
            // the first request may have succeeded even though its result was not observed.
            return new RoleMenuEditCommitResult(
                RoleMenuEditCommitStatus.PanelOutcomeUnknown,
                [new RoleMenuEditFailure(RoleMenuEditFailurePhase.PanelUpdate, exception)]);
        }
    }
}
