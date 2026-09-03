using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "MongoDB readiness is unavailable. FailureKind={FailureKind}")]
    internal static partial void MongoReadinessUnavailable(ILogger logger, string failureKind);

    [LoggerMessage(Level = LogLevel.Information, Message = "MongoDB readiness recovered")]
    internal static partial void MongoReadinessRecovered(ILogger logger);
}
