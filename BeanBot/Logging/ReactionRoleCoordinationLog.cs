using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Reaction-role mutation coordination reached its {Capacity} active-key limit; dropping additional work")]
    internal static partial void ReactionRoleCoordinationCapacityExceeded(ILogger logger, int capacity);
}
