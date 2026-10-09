using BeanBot.Persistence.Models;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuPanelLookupStatus
{
    Found,
    ChannelMissing,
    MessageMissing,
    UnexpectedChannelType
}

internal enum RoleMenuPanelDeletionStatus
{
    DeletedOrMissing,
    UnexpectedMessage,
    Failed,
    OutcomeUnknown
}

internal enum RoleMenuConfigurationDeletionStatus
{
    Deleted,
    AlreadyMissing,
    Kept,
    OutcomeUnknown
}

internal enum RoleMenuPanelDeletionIssue
{
    None,
    InvalidLocation,
    ChannelMissing,
    MessageMissing,
    UnexpectedChannelType,
    GuildMismatch,
    ChannelMismatch,
    MessageMismatch,
    UnexpectedAuthor,
    MissingManageButton,
    InvalidLookup,
    DeletionFailed,
    ReconciliationFailed
}

internal enum RoleMenuDeletionFailurePhase
{
    PanelLookup,
    PanelDeletion,
    PanelReconciliation,
    PersistenceDeletion,
    PersistenceReconciliation
}

internal sealed record RoleMenuDeletionFailure(
    RoleMenuDeletionFailurePhase Phase,
    Exception Exception);

internal sealed record RoleMenuPanelLookupResult(
    RoleMenuPanelLookupStatus Status,
    RoleMenuPanelSnapshot? Panel = null);

internal sealed record RoleMenuDeletionOperations(
    Func<ObjectId, ulong, CancellationToken, Task<RoleMenuSettings?>> ReadSettings,
    Func<ObjectId, ulong, ulong, CancellationToken, Task<RoleMenuPanelLookupResult>> ReadPanel,
    Func<RoleMenuPanelSnapshot, CancellationToken, Task<bool>> DeletePanel,
    Func<ObjectId, ulong, CancellationToken, Task<bool>> DeleteSettings,
    Func<bool> IsShuttingDown);

internal sealed record RoleMenuDeletionResult
{
    internal RoleMenuDeletionResult(
        RoleMenuConfigurationDeletionStatus configurationStatus,
        RoleMenuPanelDeletionStatus panelStatus,
        RoleMenuPanelDeletionIssue panelIssue = RoleMenuPanelDeletionIssue.None,
        bool authorizationDenied = false,
        IReadOnlyList<RoleMenuDeletionFailure>? failures = null)
    {
        ConfigurationStatus = configurationStatus;
        PanelStatus = panelStatus;
        PanelIssue = panelIssue;
        AuthorizationDenied = authorizationDenied;
        Failures = failures ?? [];
    }

    internal RoleMenuConfigurationDeletionStatus ConfigurationStatus { get; }

    internal RoleMenuPanelDeletionStatus PanelStatus { get; }

    internal RoleMenuPanelDeletionIssue PanelIssue { get; }

    internal bool AuthorizationDenied { get; }

    internal IReadOnlyList<RoleMenuDeletionFailure> Failures { get; }
}

internal static class RoleMenuDeletionWorkflow
{
    private sealed record PanelDeletionResult
    {
        internal PanelDeletionResult(
            RoleMenuPanelDeletionStatus status,
            RoleMenuPanelDeletionIssue issue,
            IReadOnlyList<RoleMenuDeletionFailure>? failures = null)
        {
            Status = status;
            Issue = issue;
            Failures = failures ?? [];
        }

        internal RoleMenuPanelDeletionStatus Status { get; init; }

        internal RoleMenuPanelDeletionIssue Issue { get; init; }

        internal IReadOnlyList<RoleMenuDeletionFailure> Failures { get; init; }
    }

    private sealed record ConfigurationDeletionReconciliationResult(
        RoleMenuConfigurationDeletionStatus Status,
        Exception? Exception = null);

    internal static async Task<RoleMenuDeletionResult> ExecuteAsync(
        ObjectId menuId,
        ulong guildId,
        ulong botUserId,
        bool administratorCanManageRoles,
        RoleMenuDeletionOperations operations,
        CancellationToken cancellationToken)
    {
        if (menuId == ObjectId.Empty)
        {
            throw new ArgumentException("A role menu ID is required.", nameof(menuId));
        }

        ArgumentNullException.ThrowIfNull(operations);
        ValidateOperations(operations);

        if (!administratorCanManageRoles)
        {
            return new RoleMenuDeletionResult(
                RoleMenuConfigurationDeletionStatus.Kept,
                RoleMenuPanelDeletionStatus.DeletedOrMissing,
                authorizationDenied: true);
        }

        var settings = await operations.ReadSettings(menuId, guildId, cancellationToken);
        if (settings is null)
        {
            return new RoleMenuDeletionResult(
                RoleMenuConfigurationDeletionStatus.AlreadyMissing,
                RoleMenuPanelDeletionStatus.DeletedOrMissing);
        }

        var panelResult = await DeletePanelAsync(
            settings,
            guildId,
            botUserId,
            operations,
            cancellationToken);
        if (panelResult.Status is RoleMenuPanelDeletionStatus.Failed
            or RoleMenuPanelDeletionStatus.OutcomeUnknown)
        {
            return new RoleMenuDeletionResult(
                RoleMenuConfigurationDeletionStatus.Kept,
                panelResult.Status,
                panelResult.Issue,
                failures: panelResult.Failures);
        }

        try
        {
            var deleted = await operations.DeleteSettings(
                menuId,
                guildId,
                cancellationToken);
            return new RoleMenuDeletionResult(
                deleted
                    ? RoleMenuConfigurationDeletionStatus.Deleted
                    : RoleMenuConfigurationDeletionStatus.AlreadyMissing,
                panelResult.Status,
                panelResult.Issue,
                failures: panelResult.Failures);
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception exception)
        {
            var reconciliation = await ReconcileConfigurationDeletionAsync(
                menuId,
                guildId,
                operations);
            var failures = new List<RoleMenuDeletionFailure>(panelResult.Failures)
            {
                new(RoleMenuDeletionFailurePhase.PersistenceDeletion, exception)
            };
            if (reconciliation.Exception is not null)
            {
                failures.Add(new RoleMenuDeletionFailure(
                    RoleMenuDeletionFailurePhase.PersistenceReconciliation,
                    reconciliation.Exception));
            }

            return new RoleMenuDeletionResult(
                reconciliation.Status,
                panelResult.Status,
                panelResult.Issue,
                failures: failures);
        }
    }

    private static async Task<PanelDeletionResult> DeletePanelAsync(
        RoleMenuSettings settings,
        ulong guildId,
        ulong botUserId,
        RoleMenuDeletionOperations operations,
        CancellationToken cancellationToken)
    {
        if (!RoleMenuCustomIds.TryParseSnowflake(settings.ChannelId, out var channelId)
            || !RoleMenuCustomIds.TryParseSnowflake(settings.MessageId, out var messageId))
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.UnexpectedMessage,
                RoleMenuPanelDeletionIssue.InvalidLocation);
        }

        var failurePhase = RoleMenuDeletionFailurePhase.PanelLookup;
        try
        {
            var lookup = await operations.ReadPanel(
                settings.Id,
                channelId,
                messageId,
                cancellationToken);
            var inspected = InspectPanel(
                lookup,
                guildId,
                channelId,
                messageId,
                botUserId);
            if (inspected.Status != RoleMenuPanelDeletionStatus.Failed
                || inspected.Issue != RoleMenuPanelDeletionIssue.None
                || lookup.Panel is null)
            {
                return inspected;
            }

            failurePhase = RoleMenuDeletionFailurePhase.PanelDeletion;
            var deleted = await operations.DeletePanel(lookup.Panel, cancellationToken);
            if (!deleted)
            {
                return new PanelDeletionResult(
                    RoleMenuPanelDeletionStatus.Failed,
                    RoleMenuPanelDeletionIssue.DeletionFailed);
            }

            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.DeletedOrMissing,
                RoleMenuPanelDeletionIssue.None);
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception exception)
        {
            return await ReconcilePanelDeletionAsync(
                channelId,
                messageId,
                guildId,
                botUserId,
                settings.Id,
                operations,
                new RoleMenuDeletionFailure(failurePhase, exception));
        }
    }

    private static PanelDeletionResult InspectPanel(
        RoleMenuPanelLookupResult lookup,
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong botUserId)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (lookup.Status == RoleMenuPanelLookupStatus.ChannelMissing)
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.DeletedOrMissing,
                RoleMenuPanelDeletionIssue.ChannelMissing);
        }

        if (lookup.Status == RoleMenuPanelLookupStatus.MessageMissing)
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.DeletedOrMissing,
                RoleMenuPanelDeletionIssue.MessageMissing);
        }

        if (lookup.Status == RoleMenuPanelLookupStatus.UnexpectedChannelType)
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.UnexpectedMessage,
                RoleMenuPanelDeletionIssue.UnexpectedChannelType);
        }

        var panel = lookup.Panel;
        if (lookup.Status != RoleMenuPanelLookupStatus.Found || panel is null)
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.UnexpectedMessage,
                RoleMenuPanelDeletionIssue.InvalidLookup);
        }

        if (panel.GuildId != guildId)
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.UnexpectedMessage,
                RoleMenuPanelDeletionIssue.GuildMismatch);
        }

        if (panel.ChannelId != channelId)
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.UnexpectedMessage,
                RoleMenuPanelDeletionIssue.ChannelMismatch);
        }

        if (panel.MessageId != messageId)
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.UnexpectedMessage,
                RoleMenuPanelDeletionIssue.MessageMismatch);
        }

        if (panel.AuthorId != botUserId)
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.UnexpectedMessage,
                RoleMenuPanelDeletionIssue.UnexpectedAuthor);
        }

        return panel.HasManageButton
            ? new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.Failed,
                RoleMenuPanelDeletionIssue.None)
            : new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.UnexpectedMessage,
                RoleMenuPanelDeletionIssue.MissingManageButton);
    }

    private static async Task<PanelDeletionResult> ReconcilePanelDeletionAsync(
        ulong channelId,
        ulong messageId,
        ulong guildId,
        ulong botUserId,
        ObjectId menuId,
        RoleMenuDeletionOperations operations,
        RoleMenuDeletionFailure panelFailure)
    {
        using var cleanupCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        try
        {
            var lookup = await operations.ReadPanel(
                menuId,
                channelId,
                messageId,
                cleanupCancellation.Token);
            var inspected = InspectPanel(
                lookup,
                guildId,
                channelId,
                messageId,
                botUserId);
            if (inspected.Status == RoleMenuPanelDeletionStatus.Failed
                && inspected.Issue == RoleMenuPanelDeletionIssue.None)
            {
                return inspected with
                {
                    Issue = RoleMenuPanelDeletionIssue.DeletionFailed,
                    Failures = [panelFailure]
                };
            }

            return inspected with { Failures = [panelFailure] };
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception reconciliationException)
        {
            return new PanelDeletionResult(
                RoleMenuPanelDeletionStatus.OutcomeUnknown,
                RoleMenuPanelDeletionIssue.ReconciliationFailed,
                [
                    panelFailure,
                    new RoleMenuDeletionFailure(
                        RoleMenuDeletionFailurePhase.PanelReconciliation,
                        reconciliationException)
                ]);
        }
    }

    private static async Task<ConfigurationDeletionReconciliationResult>
        ReconcileConfigurationDeletionAsync(
            ObjectId menuId,
            ulong guildId,
            RoleMenuDeletionOperations operations)
    {
        using var cleanupCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        try
        {
            var settings = await operations.ReadSettings(
                menuId,
                guildId,
                cleanupCancellation.Token);
            return new ConfigurationDeletionReconciliationResult(
                settings is null
                    ? RoleMenuConfigurationDeletionStatus.AlreadyMissing
                    : RoleMenuConfigurationDeletionStatus.Kept);
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ConfigurationDeletionReconciliationResult(
                RoleMenuConfigurationDeletionStatus.OutcomeUnknown,
                exception);
        }
    }

    private static void ValidateOperations(RoleMenuDeletionOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations.ReadSettings);
        ArgumentNullException.ThrowIfNull(operations.ReadPanel);
        ArgumentNullException.ThrowIfNull(operations.DeletePanel);
        ArgumentNullException.ThrowIfNull(operations.DeleteSettings);
        ArgumentNullException.ThrowIfNull(operations.IsShuttingDown);
    }
}
