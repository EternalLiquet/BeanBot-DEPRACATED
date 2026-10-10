using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Active-instance lease acquired for Discord bot {BotIdentity}")]
    internal static partial void InstanceLeaseAcquired(ILogger logger, string botIdentity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another BeanBot process already owns the active-instance lease for Discord bot {BotIdentity}")]
    internal static partial void InstanceLeaseConflict(ILogger logger, string botIdentity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Active-instance lease ownership could not be confirmed for Discord bot {BotIdentity}")]
    internal static partial void InstanceLeaseOwnershipUnknown(ILogger logger, string botIdentity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Active-instance lease renewal is temporarily uncertain for Discord bot {BotIdentity}")]
    internal static partial void InstanceLeaseRenewalUncertain(ILogger logger, string botIdentity);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Active-instance lease ownership was lost or could no longer be proven for Discord bot {BotIdentity}; requesting application shutdown")]
    internal static partial void InstanceLeaseLost(ILogger logger, string botIdentity);

    [LoggerMessage(Level = LogLevel.Information, Message = "Active-instance lease released for Discord bot {BotIdentity}")]
    internal static partial void InstanceLeaseReleased(ILogger logger, string botIdentity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Active-instance lease release found no lease owned by this process for Discord bot {BotIdentity}; expiry or takeover will provide fencing")]
    internal static partial void InstanceLeaseReleaseNotOwned(ILogger logger, string botIdentity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Active-instance lease release could not be confirmed for Discord bot {BotIdentity}; allowing lease expiry to provide eventual takeover")]
    internal static partial void InstanceLeaseReleaseFailed(ILogger logger, string botIdentity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Active-instance lease renewal loop did not drain within its shutdown bound")]
    internal static partial void InstanceLeaseRenewalDrainTimedOut(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping active-instance lease release because side-effecting Discord work did not drain safely; process exit and lease expiry will provide handoff")]
    internal static partial void InstanceLeaseReleaseSkipped(ILogger logger);
}
