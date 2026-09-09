using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class PunScheduleLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Next pun scheduled for {NextLocal} in {TimeZoneId} ({NextUtc} UTC). Now: {NowLocal}")]
    internal static partial void Scheduled(
        ILogger logger,
        string nextLocal,
        string nextUtc,
        string nowLocal,
        string timeZoneId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Posting daily pun at {LocalTime} in {TimeZoneId}")]
    internal static partial void Posting(
        ILogger logger,
        DateTimeOffset localTime,
        string timeZoneId);
}
