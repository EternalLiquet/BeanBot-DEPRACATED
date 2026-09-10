using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class EditMessageLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Dropping edited-message handling because the {MaximumInFlightOperations} operation capacity is full")]
    internal static partial void AdmissionCapacityExceeded(
        ILogger logger,
        int maximumInFlightOperations);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Timed out draining {InFlightOperationCount} edited-message operation(s) after {DrainTimeout}; Discord teardown will stay blocked while they remain active")]
    internal static partial void DrainTimedOut(
        ILogger logger,
        int inFlightOperationCount,
        TimeSpan drainTimeout);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "An edited-message operation failed unexpectedly")]
    internal static partial void OperationFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A timed-out or canceled edited-message Discord operation failed after its bounded wait ended")]
    internal static partial void LateDiscordOperationFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not resolve original Discord message {MessageId} while processing an edited fortune request")]
    internal static partial void OriginalMessageLookupFailed(
        ILogger logger,
        ulong messageId,
        Exception exception);
}
