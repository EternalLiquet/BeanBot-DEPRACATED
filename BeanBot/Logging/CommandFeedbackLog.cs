using Discord.Commands;
using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not send legacy command failure feedback. CommandError={CommandError}")]
    internal static partial void LegacyCommandFeedbackDeliveryFailed(
        ILogger logger,
        CommandError? commandError,
        Exception exception);
}
