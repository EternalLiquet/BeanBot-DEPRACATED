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

internal sealed record RoleMenuPublicationResult(
    RoleMenuPublicationStatus Status,
    ulong? MessageId)
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
            catch (Exception)
            {
                panel = await TryReconcilePanelAsync(
                    draft,
                    botUserId,
                    existingSettings,
                    operations);
                if (panel is null)
                {
                    return new RoleMenuPublicationResult(
                        RoleMenuPublicationStatus.PanelOutcomeUnknown,
                        null);
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
        catch (Exception)
        {
            return await ReconcilePersistenceAsync(
                draft,
                panel,
                operations);
        }

        return new RoleMenuPublicationResult(
            RoleMenuPublicationStatus.Published,
            panel.MessageId);
    }

    private static async Task<RoleMenuPublicationResult> ReconcilePersistenceAsync(
        RoleMenuDraft draft,
        RoleMenuPanelSnapshot panel,
        RoleMenuPublicationOperations operations)
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
            catch (Exception)
            {
                return new RoleMenuPublicationResult(
                    RoleMenuPublicationStatus.PersistenceOutcomeUnknown,
                    panel.MessageId);
            }
        }

        if (RoleMenuPublicationSettings.Matches(
                persistedSettings,
                draft,
                panel.MessageId))
        {
            return new RoleMenuPublicationResult(
                RoleMenuPublicationStatus.Published,
                panel.MessageId);
        }

        if (persistedSettings is not null)
        {
            return new RoleMenuPublicationResult(
                RoleMenuPublicationStatus.PersistenceOutcomeUnknown,
                panel.MessageId);
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
            catch (Exception)
            {
                // A failed or timed-out rollback leaves the known orphan panel in place.
            }
        }

        return rollbackSucceeded
            ? new RoleMenuPublicationResult(
                RoleMenuPublicationStatus.PersistenceAbsentPanelRolledBack,
                null)
            : new RoleMenuPublicationResult(
                RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed,
                panel.MessageId);
    }

    private static async Task<RoleMenuPanelSnapshot?> TryReconcilePanelAsync(
        RoleMenuDraft draft,
        ulong botUserId,
        RoleMenuSettings? existingSettings,
        RoleMenuPublicationOperations operations)
    {
        using var reconciliationCancellation = CreateCleanupCancellation();
        try
        {
            return await FindExistingPanelAsync(
                draft,
                botUserId,
                existingSettings,
                operations,
                reconciliationCancellation.Token);
        }
        catch (Exception)
        {
            return null;
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
