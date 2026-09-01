using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Discord paginator {Operation} could not start because all {MaximumOperations} tracked Discord operation slots are occupied")]
    internal static partial void PaginatorDiscordOperationCapacityExhausted(
        ILogger logger,
        string operation,
        int maximumOperations);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Discord paginator {Operation} exceeded its {Timeout} operation timeout; underlying Discord work remains owned until it settles")]
    internal static partial void PaginatorDiscordOperationTimedOut(
        ILogger logger,
        string operation,
        TimeSpan timeout);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Discord paginator {Operation} failed after its bounded wait ended")]
    internal static partial void PaginatorDiscordOperationLateFailure(
        ILogger logger,
        string operation,
        Exception exception);
}
