using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping reaction-role {Action} for message {MessageId} and role {RoleId} because the target is not assignable: {Reason}")]
    internal static partial void ReactionRoleTargetUnassignable(
        ILogger logger,
        string action,
        ulong messageId,
        ulong roleId,
        string reason);
}
