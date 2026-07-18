using BeanBot.Util;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace BeanBot.Tests.Util;

public class DiscordOwnerErrorSinkTests
{
    [Fact]
    public void FormatAlert_IncludesRenderedMessageAndException()
    {
        var logEvent = CreateLogEvent("Something broke", new InvalidOperationException("very broken"));

        var alert = DiscordOwnerErrorSink.FormatAlert(logEvent);

        Assert.Contains("Something broke", alert);
        Assert.Contains("InvalidOperationException", alert);
        Assert.Contains("very broken", alert);
    }

    [Fact]
    public void FormatAlert_StaysWithinDiscordMessageLimit()
    {
        var logEvent = CreateLogEvent(new string('x', 3000));

        var alert = DiscordOwnerErrorSink.FormatAlert(logEvent);

        Assert.True(alert.Length <= 1900);
        Assert.EndsWith("...(truncated)", alert);
    }

    private static LogEvent CreateLogEvent(string message, Exception? exception = null)
        => new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception,
            new MessageTemplateParser().Parse(message),
            Array.Empty<LogEventProperty>());
}
