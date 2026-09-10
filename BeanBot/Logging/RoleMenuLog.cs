using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Role menu settings saved idempotently. MenuId={MenuId}")]
    internal static partial void RoleMenuSettingsSaved(ILogger logger, ObjectId menuId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Role menu settings deleted. MenuId={MenuId}")]
    internal static partial void RoleMenuSettingsDeleted(ILogger logger, ObjectId menuId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu publication failed. MenuId={MenuId}")]
    internal static partial void RoleMenuPublicationFailed(ILogger logger, string menuId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu panel reconciliation failed after an uncertain Discord publication outcome. MenuId={MenuId}")]
    internal static partial void RoleMenuPanelReconciliationFailed(
        ILogger logger,
        string menuId,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu persistence reconciliation failed after an uncertain MongoDB outcome. MenuId={MenuId}")]
    internal static partial void RoleMenuPersistenceReconciliationFailed(
        ILogger logger,
        string menuId,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu was committed but its private success confirmation could not be delivered. MenuId={MenuId}")]
    internal static partial void RoleMenuPublicationConfirmationFailed(
        ILogger logger,
        string menuId,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Role menu publication rollback could not delete the orphaned panel. MenuId={MenuId}")]
    internal static partial void RoleMenuPublicationRollbackFailed(ILogger logger, string menuId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu deletion could not delete its published panel. MenuId={MenuId}")]
    internal static partial void RoleMenuPanelDeletionFailed(ILogger logger, string menuId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu deletion failed before its final state could be determined. MenuId={MenuId}")]
    internal static partial void RoleMenuDeletionFailed(ILogger logger, string menuId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu deletion was interrupted. MenuId={MenuId}, Phase={Phase}, KnownState={KnownState}")]
    internal static partial void RoleMenuDeletionInterrupted(
        ILogger logger,
        string menuId,
        string phase,
        string knownState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu deletion could not reconcile the published panel after an uncertain delete outcome. MenuId={MenuId}")]
    internal static partial void RoleMenuPanelDeletionReconciliationFailed(
        ILogger logger,
        string menuId,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Persisted role-menu settings deletion reported an error. MenuId={MenuId}")]
    internal static partial void RoleMenuPersistenceDeletionFailed(ILogger logger, string menuId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Role menu deletion could not reconcile persisted settings after an uncertain delete outcome. MenuId={MenuId}")]
    internal static partial void RoleMenuDeletionReconciliationFailed(
        ILogger logger,
        string menuId,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu role mutation failed. MenuId={MenuId}, Action={Action}, RoleId={RoleId}, RoleName={RoleName}")]
    internal static partial void RoleMenuMutationFailed(
        ILogger logger,
        string menuId,
        string action,
        string roleId,
        string roleName,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu configuration is stale or invalid. MenuId={MenuId}, Reason={Reason}")]
    internal static partial void RoleMenuConfigurationInvalid(
        ILogger logger,
        string menuId,
        string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Role menu selection completed. MenuId={MenuId}, Added={AddedCount}, Removed={RemovedCount}, Failed={FailureCount}")]
    internal static partial void RoleMenuSelectionCompleted(
        ILogger logger,
        ObjectId menuId,
        int addedCount,
        int removedCount,
        int failureCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu selection failed before completion. MenuId={MenuId}")]
    internal static partial void RoleMenuSelectionFailed(
        ILogger logger,
        string menuId,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu role mutation was interrupted. MenuId={MenuId}, Action={Action}, RoleId={RoleId}, RoleName={RoleName}, Outcome={Outcome}")]
    internal static partial void RoleMenuMutationInterrupted(
        ILogger logger,
        string menuId,
        string action,
        string roleId,
        string roleName,
        string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role menu could not reconcile member roles after a partial or uncertain mutation. MenuId={MenuId}")]
    internal static partial void RoleMenuReconciliationFailed(
        ILogger logger,
        string menuId,
        Exception exception);
}
