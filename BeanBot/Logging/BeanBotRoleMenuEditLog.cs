using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Role-menu edit persistence outcome was not confirmed for menu {MenuId}")]
    internal static partial void RoleMenuEditPersistenceFailed(
        ILogger logger,
        string menuId,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Role-menu edit panel update outcome was not confirmed for menu {MenuId}")]
    internal static partial void RoleMenuEditPanelUpdateFailed(
        ILogger logger,
        string menuId,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Role-menu edit operation failed unexpectedly for menu {MenuId}")]
    internal static partial void RoleMenuEditOperationFailed(
        ILogger logger,
        string menuId,
        Exception exception);
}
