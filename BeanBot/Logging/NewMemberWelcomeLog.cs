using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "New-member welcome capacity is full; dropping delivery for user {UserId}. MaximumOutstanding={MaximumOutstanding}")]
    internal static partial void WelcomeCapacityFull(ILogger logger, ulong userId, int maximumOutstanding);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Suppressing duplicate pending new-member welcome for user {UserId}")]
    internal static partial void WelcomeDuplicateSuppressed(ILogger logger, ulong userId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "New-member welcome delivery was canceled during shutdown for user {UserId}")]
    internal static partial void WelcomeDeliveryCanceled(ILogger logger, ulong userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "New-member welcome shutdown exceeded its {DrainTimeout} drain window; canceling remaining work")]
    internal static partial void WelcomeDrainTimedOut(ILogger logger, TimeSpan drainTimeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "New-member welcome workers did not finish within {CancellationGrace} after cancellation; process exit will reclaim remaining work")]
    internal static partial void WelcomeCancellationGraceTimedOut(ILogger logger, TimeSpan cancellationGrace);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord new-member welcome {Operation} failed after BeanBot stopped waiting. UserId={UserId}")]
    internal static partial void WelcomeDeliveryLateFailure(ILogger logger, string operation, ulong userId, Exception exception);
}
