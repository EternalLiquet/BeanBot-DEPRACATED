using Discord;
using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Discord application commands registered successfully")]
    internal static partial void InteractionCommandsRegistered(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord application-command registration failed")]
    internal static partial void InteractionCommandRegistrationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord interaction execution failed. InteractionType={InteractionType}, Reason={Reason}")]
    internal static partial void InteractionCommandFailed(ILogger logger, InteractionType interactionType, string? reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord interaction execution threw unexpectedly. InteractionType={InteractionType}")]
    internal static partial void InteractionCommandThrew(ILogger logger, InteractionType interactionType, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send the safe failure response for Discord interaction type {InteractionType}")]
    internal static partial void InteractionFailureResponseFailed(ILogger logger, InteractionType interactionType, Exception exception);
}
