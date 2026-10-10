using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Reaction-role mutation coordination reached its {Capacity} active-key limit; dropping additional work")]
    internal static partial void ReactionRoleCoordinationCapacityExceeded(ILogger logger, int capacity);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saved role panel cleanup failed after a Discord deletion event")]
    internal static partial void RolePanelDeletionCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saved role panel cleanup timed out after a Discord deletion event")]
    internal static partial void RolePanelDeletionCleanupTimedOut(ILogger logger);
}
