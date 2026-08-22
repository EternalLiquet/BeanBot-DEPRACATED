using BeanBot.Persistence.Models;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal sealed record RoleMenuPanelSnapshot(
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    ulong AuthorId,
    bool HasManageButton);

internal sealed record RoleMenuPublicationOperations(
    Func<ObjectId, ulong, CancellationToken, Task<RoleMenuSettings?>> ReadSettings,
    Func<ulong, ulong, CancellationToken, Task<RoleMenuPanelSnapshot?>> ReadExactPanel,
    Func<ulong, int, CancellationToken, Task<IReadOnlyList<RoleMenuPanelSnapshot>>>
        ReadRecentPanels,
    Func<RoleMenuDraft, CancellationToken, Task<RoleMenuPanelSnapshot>> SendPanel,
    Func<RoleMenuSettings, CancellationToken, Task> UpsertSettings,
    Func<RoleMenuPanelSnapshot, CancellationToken, Task<bool>> DeletePanel);

internal enum RoleMenuPublicationStatus
{
    Published,
    PanelOutcomeUnknown,
    PersistenceAbsentPanelRolledBack,
    PersistenceAbsentRollbackFailed,
    PersistenceOutcomeUnknown
}

internal enum RoleMenuPublicationFailurePhase
{
    PanelPublication,
    PanelReconciliation,
    Persistence,
    PersistenceReconciliation,
    PanelRollback
}

internal sealed record RoleMenuPublicationFailure(
    RoleMenuPublicationFailurePhase Phase,
    Exception Exception);

internal sealed record RoleMenuPublicationResult(
    RoleMenuPublicationStatus Status,
    ulong? MessageId,
    IReadOnlyList<RoleMenuPublicationFailure> Failures)
{
    internal bool CanRetry
        => Status == RoleMenuPublicationStatus.PersistenceAbsentPanelRolledBack;

    internal bool IsTerminal => !CanRetry;
}

internal static class RoleMenuPublicationWorkflow
{
    internal static async Task<RoleMenuPublicationResult> ExecuteAsync(
        RoleMenuDraft draft,
        ulong botUserId,
        RoleMenuPublicationOperations operations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(operations);
        ValidateOperations(operations);
        var failures = new List<RoleMenuPublicationFailure>();

        var existingSettings = await operations.ReadSettings(
            draft.MenuId,
            draft.GuildId,
            cancellationToken);
        var panel = await FindExistingPanelAsync(
            draft,
            botUserId,
            existingSettings,
            operations,
            cancellationToken);
        if (panel is null)
        {
            try
            {
                panel = await operations.SendPanel(draft, cancellationToken);
                if (!IsExpectedPanel(panel, draft, botUserId))
                {
                    throw new InvalidOperationException(
                        "The published role-menu panel did not match the attempted publication.");
                }
            }
            catch (Exception exception)
            {
                failures.Add(new RoleMenuPublicationFailure(
                    RoleMenuPublicationFailurePhase.PanelPublication,
                    exception));
                var reconciliation = await TryReconcilePanelAsync(
                    draft,
                    botUserId,
                    existingSettings,
                    operations);
                panel = reconciliation.Panel;
                if (reconciliation.Exception is not null)
                {
                    failures.Add(new RoleMenuPublicationFailure(
                        RoleMenuPublicationFailurePhase.PanelReconciliation,
                        reconciliation.Exception));
                }

                if (panel is null)
                {
                    return new RoleMenuPublicationResult(
                        RoleMenuPublicationStatus.PanelOutcomeUnknown,
                        null,
                        failures);
                }
            }
        }

        var settings = RoleMenuPublicationSettings.Create(
            draft,
            panel.MessageId,
            existingSettings?.CreatedAtUtc ?? default);
        try
        {
            await operations.UpsertSettings(settings, cancellationToken);
        }
        catch (Exception exception)
        {
            failures.Add(new RoleMenuPublicationFailure(
                RoleMenuPublicationFailurePhase.Persistence,
                exception));
            return await ReconcilePersistenceAsync(
                draft,
                panel,
                operations,
                failures);
        }

        return new RoleMenuPublicationResult(
            RoleMenuPublicationStatus.Published,
            panel.MessageId,
            failures);
    }

    private static async Task<RoleMenuPublicationResult> ReconcilePersistenceAsync(
        RoleMenuDraft draft,
        RoleMenuPanelSnapshot panel,
        RoleMenuPublicationOperations operations,
        List<RoleMenuPublicationFailure> failures)
    {
        RoleMenuSettings? persistedSettings;
        using (var reconciliationCancellation = CreateCleanupCancellation())
        {
            try
            {
                persistedSettings = await operations.ReadSettings(
                    draft.MenuId,
                    draft.GuildId,
                    reconciliationCancellation.Token);
            }
            catch (Exception exception)
            {
                failures.Add(new RoleMenuPublicationFailure(
                    RoleMenuPublicationFailurePhase.PersistenceReconciliation,
                    exception));
                return new RoleMenuPublicationResult(
                    RoleMenuPublicationStatus.PersistenceOutcomeUnknown,
                    panel.MessageId,
                    failures);
            }
        }

        if (RoleMenuPublicationSettings.Matches(
                persistedSettings,
                draft,
                panel.MessageId))
        {
            return new RoleMenuPublicationResult(
                RoleMenuPublicationStatus.Published,
                panel.MessageId,
                failures);
        }

        if (persistedSettings is not null)
        {
            return new RoleMenuPublicationResult(
                RoleMenuPublicationStatus.PersistenceOutcomeUnknown,
                panel.MessageId,
                failures);
        }

        var rollbackSucceeded = false;
        using (var rollbackCancellation = CreateCleanupCancellation())
        {
            try
            {
                rollbackSucceeded = await operations.DeletePanel(
                    panel,
                    rollbackCancellation.Token);
            }
            catch (Exception exception)
            {
                failures.Add(new RoleMenuPublicationFailure(
                    RoleMenuPublicationFailurePhase.PanelRollback,
                    exception));
                // A failed or timed-out rollback leaves the known orphan panel in place.
            }
        }

        return rollbackSucceeded
            ? new RoleMenuPublicationResult(
                RoleMenuPublicationStatus.PersistenceAbsentPanelRolledBack,
                null,
                failures)
            : new RoleMenuPublicationResult(
                RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed,
                panel.MessageId,
                failures);
    }

    private sealed record PanelReconciliationResult(
        RoleMenuPanelSnapshot? Panel,
        Exception? Exception = null);

    private static async Task<PanelReconciliationResult> TryReconcilePanelAsync(
        RoleMenuDraft draft,
        ulong botUserId,
        RoleMenuSettings? existingSettings,
        RoleMenuPublicationOperations operations)
    {
        using var reconciliationCancellation = CreateCleanupCancellation();
        try
        {
            var panel = await FindExistingPanelAsync(
                draft,
                botUserId,
                existingSettings,
                operations,
                reconciliationCancellation.Token);
            return new PanelReconciliationResult(panel);
        }
        catch (Exception exception)
        {
            return new PanelReconciliationResult(null, exception);
        }
    }

    private static async Task<RoleMenuPanelSnapshot?> FindExistingPanelAsync(
        RoleMenuDraft draft,
        ulong botUserId,
        RoleMenuSettings? existingSettings,
        RoleMenuPublicationOperations operations,
        CancellationToken cancellationToken)
    {
        if (existingSettings is not null
            && RoleMenuCustomIds.TryParseSnowflake(
                existingSettings.ChannelId,
                out var existingChannelId)
            && existingChannelId == draft.TargetChannelId
            && RoleMenuCustomIds.TryParseSnowflake(
                existingSettings.MessageId,
                out var existingMessageId))
        {
            var exactPanel = await operations.ReadExactPanel(
                draft.TargetChannelId,
                existingMessageId,
                cancellationToken);
            if (IsExpectedPanel(exactPanel, draft, botUserId))
            {
                return exactPanel;
            }
        }

        var recentPanels = await operations.ReadRecentPanels(
            draft.TargetChannelId,
            RoleMenuConstants.PanelReconciliationSearchLimit,
            cancellationToken);
        ArgumentNullException.ThrowIfNull(recentPanels);
        return recentPanels.FirstOrDefault(
            panel => IsExpectedPanel(panel, draft, botUserId));
    }

    private static bool IsExpectedPanel(
        RoleMenuPanelSnapshot? panel,
        RoleMenuDraft draft,
        ulong botUserId)
        => panel is not null
           && panel.GuildId == draft.GuildId
           && panel.ChannelId == draft.TargetChannelId
           && panel.MessageId != 0
           && panel.AuthorId == botUserId
           && panel.HasManageButton;

    private static CancellationTokenSource CreateCleanupCancellation()
        => new(RoleMenuConstants.CleanupTimeout);

    private static void ValidateOperations(RoleMenuPublicationOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations.ReadSettings);
        ArgumentNullException.ThrowIfNull(operations.ReadExactPanel);
        ArgumentNullException.ThrowIfNull(operations.ReadRecentPanels);
        ArgumentNullException.ThrowIfNull(operations.SendPanel);
        ArgumentNullException.ThrowIfNull(operations.UpsertSettings);
        ArgumentNullException.ThrowIfNull(operations.DeletePanel);
    }
}
