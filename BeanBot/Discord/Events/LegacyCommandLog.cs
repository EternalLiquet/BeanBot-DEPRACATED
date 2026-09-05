using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

internal static partial class LegacyCommandLog
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rejected a legacy command because {ActiveExecutionCount} execution(s) already occupy the configured limit {MaximumConcurrentExecutions}")]
    internal static partial void CapacityRejected(
        ILogger logger,
        int activeExecutionCount,
        int maximumConcurrentExecutions);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rejected a legacy command because command execution is stopping")]
    internal static partial void ShutdownRejected(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Timed out draining {SurvivingExecutionCount} legacy command execution(s) after {DrainTimeout}; Discord teardown will be skipped while known command work survives")]
    internal static partial void DrainTimedOut(
        ILogger logger,
        int survivingExecutionCount,
        TimeSpan drainTimeout);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "A legacy command failed after the bounded command-drain wait ended")]
    internal static partial void LateFailure(ILogger logger, Exception exception);
}
