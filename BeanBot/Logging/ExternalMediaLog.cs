using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "External media command failed during {Stage} for {MediaSource}. ErrorType={ErrorType}")]
    internal static partial void ExternalMediaCommandFailed(
        ILogger logger,
        string stage,
        string mediaSource,
        string errorType);
}
