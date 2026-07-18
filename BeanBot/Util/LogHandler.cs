using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Serilog;

using System.IO;
using System.Threading.Tasks;

namespace BeanBot.Util
{
    public static class LogHandler
    {
        public static void CreateLoggerConfiguration()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console()
                .WriteTo.Async(a => a.File(Path.Combine(DirectorySetup.botBaseDirectory, "Logs", "BeanBotLogs.txt"), rollingInterval: RollingInterval.Day))
                .WriteTo.Sink(new DiscordOwnerErrorSink())
                .CreateLogger();
            Log.Information("Logger Configuration complete");
        }

        public static Task LogMessages(LogMessage messages)
        {
            var formattedMessage = string.IsNullOrWhiteSpace(messages.Source)
                ? messages.Message ?? messages.ToString()
                : $"Discord:\t{messages.Source}\t{messages.Message}";

            switch (messages.Severity)
            {
                case LogSeverity.Critical:
                    Log.Fatal(messages.Exception, "{DiscordMessage}", formattedMessage);
                    break;
                case LogSeverity.Error:
                    Log.Error(messages.Exception, "{DiscordMessage}", formattedMessage);
                    break;
                case LogSeverity.Warning:
                    Log.Warning(messages.Exception, "{DiscordMessage}", formattedMessage);
                    break;
                case LogSeverity.Info:
                    Log.Information(messages.Exception, "{DiscordMessage}", formattedMessage);
                    break;
                case LogSeverity.Verbose:
                    Log.Verbose(messages.Exception, "{DiscordMessage}", formattedMessage);
                    break;
                case LogSeverity.Debug:
                    Log.Debug(messages.Exception, "{DiscordMessage}", formattedMessage);
                    break;
                default:
                    Log.Information(messages.Exception, "Discord log ({Severity}): {DiscordMessage}", messages.Severity, formattedMessage);
                    break;
            }

            return Task.CompletedTask;
        }

        public static Task LogNewMember(SocketGuildUser newUser)
        {
            Log.Information("Discord user {Username} ({UserId}) joined {Guild}", newUser.Username, newUser.Id, newUser.Guild);
            return Task.CompletedTask;
        }

        public static Task LogCommands(Optional<CommandInfo> command, ICommandContext context, IResult result)
        {
            var commandName = command.IsSpecified ? command.Value.Name : "Unspecified Command";
            if (result.IsSuccess)
            {
                Log.Information("Discord command {CommandName} was executed", commandName);
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
                    Log.Warning(
                        "Discord command {CommandName} was rejected with {Error}: {Reason}. Input: {Input}",
                        commandName,
                        result.Error,
                        result.ErrorReason,
                        context.Message);
                }
                else
                {
                    Log.Error(
                        "Discord command {CommandName} failed with {Error}: {Reason}. Input: {Input}",
                        commandName,
                        result.Error,
                        result.ErrorReason,
                        context.Message);
                }
            }
            return Task.CompletedTask;
        }
    }
}
