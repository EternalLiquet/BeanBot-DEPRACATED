using System.Globalization;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace BeanBot.Util;

public sealed class LogHandler
{
    private readonly ILogger<LogHandler> _logger;

    public LogHandler(ILogger<LogHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal static Serilog.ILogger CreateBootstrapLogger()
        => new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

    internal static void ConfigureLogger(
        LoggerConfiguration loggerConfiguration,
        IOwnerErrorNotifier ownerErrorNotifier)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(ownerErrorNotifier);

        loggerConfiguration
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.Async(a => a.File(
                Path.Combine(DirectorySetup.botBaseDirectory, "Logs", "BeanBotLogs.txt"),
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day))
            .WriteTo.Sink(new DiscordOwnerErrorSink(ownerErrorNotifier));
    }

    public Task LogMessages(LogMessage messages)
    {
        var formattedMessage = string.IsNullOrWhiteSpace(messages.Source)
            ? messages.Message ?? messages.ToString()
            : $"Discord:\t{messages.Source}\t{messages.Message}";

        var severity = messages.Severity;
        var logLevel = severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Trace,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.None
        };
        if (logLevel == LogLevel.None)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                BeanBotLog.DiscordMessageFallback(
                    _logger,
                    severity,
                    formattedMessage,
                    messages.Exception);
            }
        }
        else
        {
            BeanBotLog.DiscordMessage(
                _logger,
                logLevel,
                formattedMessage,
                messages.Exception);
        }

        return Task.CompletedTask;
    }

    public Task LogNewMember(SocketGuildUser newUser)
    {
        BeanBotLog.DiscordUserJoined(
            _logger,
            newUser.Username,
            newUser.Id,
            newUser.Guild);
        return Task.CompletedTask;
    }

    public Task LogCommands(Optional<CommandInfo> command, ICommandContext context, IResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        var commandName = command.IsSpecified ? command.Value.Name : "Unspecified Command";
        if (result.IsSuccess)
        {
            BeanBotLog.DiscordCommandExecuted(_logger, commandName);
        }
        else
        {
            var isExpectedUserError = result.Error == CommandError.UnknownCommand ||
                result.Error == CommandError.ParseFailed ||
                result.Error == CommandError.BadArgCount ||
                result.Error == CommandError.ObjectNotFound ||
                result.Error == CommandError.MultipleMatches ||
                result.Error == CommandError.UnmetPrecondition;
            if (isExpectedUserError)
            {
                BeanBotLog.DiscordCommandRejected(
                    _logger,
                    commandName,
                    result.Error,
                    result.ErrorReason);
            }
            else
            {
                BeanBotLog.DiscordCommandFailed(
                    _logger,
                    commandName,
                    result.Error,
                    result.ErrorReason);
            }
        }
        return Task.CompletedTask;
    }
}
