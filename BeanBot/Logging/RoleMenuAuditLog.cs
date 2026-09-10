using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Role menu audit lookup failed or timed out. MenuId={MenuId}")]
    internal static partial void RoleMenuAuditLookupFailed(
        ILogger logger,
        string menuId,
        Exception exception);
}
