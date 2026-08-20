using BeanBot.Logging;

using Discord;
using Discord.Commands;

using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Core;
using Serilog.Events;

using Xunit;

namespace BeanBot.Tests.Logging;

public class LogHandlerTests
{
    [Theory]
    [InlineData(LogSeverity.Critical, LogLevel.Critical)]
    [InlineData(LogSeverity.Error, LogLevel.Error)]
    [InlineData(LogSeverity.Warning, LogLevel.Warning)]
    [InlineData(LogSeverity.Info, LogLevel.Information)]
    [InlineData(LogSeverity.Verbose, LogLevel.Trace)]
    [InlineData(LogSeverity.Debug, LogLevel.Debug)]
    public async Task LogMessages_MapsDiscordSeverityAndPreservesStructuredMessage(
        LogSeverity severity,
        LogLevel expectedLevel)
    {
        var logger = new RecordingLogger<LogHandler>();
        var handler = new LogHandler(logger);
        var exception = new InvalidOperationException("discord failure");

        await handler.LogMessages(new LogMessage(severity, "Gateway", "test message", exception));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expectedLevel, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Equal(
            "Discord:\tGateway\ttest message",
            Assert.Single(entry.Properties, property => property.Key == "DiscordMessage").Value);
    }

    [Theory]
    [InlineData(CommandError.BadArgCount, LogLevel.Warning)]
    [InlineData(CommandError.Exception, LogLevel.Error)]
    public async Task LogCommands_LogsSafeFailureMetadataWithoutReadingMessagePayload(
        CommandError error,
        LogLevel expectedLevel)
    {
        var logger = new RecordingLogger<LogHandler>();
        var handler = new LogHandler(logger);
        var result = new FakeResult(error, "safe reason");

        await handler.LogCommands(default, new MessageThrowingCommandContext(), result);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expectedLevel, entry.Level);
        Assert.DoesNotContain(entry.Properties, property => property.Key == "Input");
        Assert.Contains(entry.Properties, property =>
            property.Key == "Error" && Equals(property.Value, error));
        Assert.Contains(entry.Properties, property =>
            property.Key == "Reason" && Equals(property.Value, "safe reason"));
    }

    [Fact]
    public void MicrosoftLogger_RoutesOnceThroughSerilogWithCategoryAndProperties()
    {
        var sink = new CapturingSerilogSink();
        using var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(serilogLogger, dispose: false);
        });
        var logger = loggerFactory.CreateLogger<LogHandlerTests>();
        var exception = new InvalidOperationException("failed");

        BeanBotLog.ReactionRoleActionFailed(logger, "add", 42UL, exception);

        var logEvent = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Error, logEvent.Level);
        Assert.Same(exception, logEvent.Exception);
        Assert.Equal(42UL, Assert.IsType<ScalarValue>(logEvent.Properties["MessageId"]).Value);
        Assert.Equal(
            typeof(LogHandlerTests).FullName,
            Assert.IsType<ScalarValue>(logEvent.Properties["SourceContext"]).Value);
    }

    [Theory]
    [InlineData("BeanBot.Services.CategoryFilterProbe", LogLevel.Trace, true)]
    [InlineData("Microsoft.AspNetCore.CategoryFilterProbe", LogLevel.Debug, false)]
    [InlineData("System.Net.Http.CategoryFilterProbe", LogLevel.Trace, false)]
    [InlineData("Microsoft.Hosting.Lifetime", LogLevel.Information, true)]
    public void ProductionConfiguration_AppliesCategoryMinimumLevels(
        string category,
        LogLevel logLevel,
        bool expectedEnabled)
    {
        var sink = new CapturingSerilogSink();
        var notifier = new CapturingNotifier();
        using var serilogLogger = CreateProductionLogger(sink, notifier);
        using var loggerFactory = CreateLoggerFactory(serilogLogger);
        var logger = loggerFactory.CreateLogger(category);

        Assert.Equal(expectedEnabled, logger.IsEnabled(logLevel));
        WriteTestLog(logger, logLevel, "category filter probe");

        if (expectedEnabled)
        {
            var logEvent = Assert.Single(sink.Events);
            Assert.Equal(
                category,
                Assert.IsType<ScalarValue>(logEvent.Properties["SourceContext"]).Value);
        }
        else
        {
            Assert.Empty(sink.Events);
        }
        Assert.Empty(notifier.Alerts);
    }

    [Fact]
    public void ProductionConfiguration_BeanBotErrorReachesOwnerSinkOnceButWarningDoesNot()
    {
        var notifier = new CapturingNotifier();
        var sink = new CapturingSerilogSink();
        using var serilogLogger = CreateProductionLogger(sink, notifier);
        using var loggerFactory = CreateLoggerFactory(serilogLogger);
        var logger = loggerFactory.CreateLogger<LogHandlerTests>();

        BeanBotLog.DiscordPresenceFailed(logger, new InvalidOperationException("handled"));
        BeanBotLog.ReactionRoleActionFailed(logger, "add", 42UL, new InvalidOperationException("failed"));

        var alert = Assert.Single(notifier.Alerts);
        Assert.Contains("Failed to \"add\" a reaction role for message 42", alert);
        Assert.DoesNotContain("Discord started", alert);
    }

    private static Serilog.Core.Logger CreateProductionLogger(
        CapturingSerilogSink sink,
        CapturingNotifier notifier)
    {
        var loggerConfiguration = new LoggerConfiguration();
        LogHandler.ConfigureLogger(loggerConfiguration, notifier);
        return loggerConfiguration
            .WriteTo.Sink(sink)
            .CreateLogger();
    }

    private static ILoggerFactory CreateLoggerFactory(Serilog.ILogger serilogLogger)
        => LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(serilogLogger, dispose: false);
        });

    private static void WriteTestLog(
        Microsoft.Extensions.Logging.ILogger logger,
        LogLevel logLevel,
        string message)
        => logger.Log(
            logLevel,
            eventId: default,
            message,
            exception: null,
            static (state, _) => state);

    private sealed record LogEntry(
        LogLevel Level,
        Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>> Properties);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? Array.Empty<KeyValuePair<string, object?>>();
            Entries.Add(new LogEntry(logLevel, exception, properties));
        }
    }

    private sealed class FakeResult : IResult
    {
        public FakeResult(CommandError error, string errorReason)
        {
            Error = error;
            ErrorReason = errorReason;
        }

        public CommandError? Error { get; }
        public string ErrorReason { get; }
        public bool IsSuccess => false;
    }

    private sealed class MessageThrowingCommandContext : ICommandContext
    {
        public IDiscordClient Client => throw new NotSupportedException();
        public IGuild Guild => throw new NotSupportedException();
        public IMessageChannel Channel => throw new NotSupportedException();
        public IUser User => throw new NotSupportedException();
        public IUserMessage Message => throw new InvalidOperationException("Message payload must not be read while logging.");
    }

    private sealed class CapturingSerilogSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class CapturingNotifier : IOwnerErrorNotifier
    {
        public List<string> Alerts { get; } = [];
        public void Enqueue(string alert) => Alerts.Add(alert);
    }
}
