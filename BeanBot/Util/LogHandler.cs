using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Serilog;

using System;
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
                .CreateLogger();
            Log.Information("Logger Configuration complete");
        }

        public static Task LogMessages(LogMessage messages)
        {
            string formattedMessage = $"Discord:\t{messages.Source.ToString()}\t{messages.Message.ToString()}";
            switch (messages.Severity)
            {
                case LogSeverity.Critical:
                    Log.Fatal(formattedMessage);
                    break;
                case LogSeverity.Error:
                    Log.Error(formattedMessage);
                    break;
                case LogSeverity.Warning:
                    Log.Warning(formattedMessage);
                    break;
                case LogSeverity.Info:
                    Log.Information(formattedMessage);
                    break;
                case LogSeverity.Verbose:
                    Log.Verbose(formattedMessage);
                    break;
                default:
                    Log.Information($"Log Severity: {messages.Severity}");
                    Log.Information(formattedMessage);
                    break;
            }
            return Task.CompletedTask;
        }

        public async static Task LogNewMember(SocketGuildUser newUser)
        {
            string formattedMessage = $"Discord:\t{newUser.Username} has joined {newUser.Guild} on {DateTime.UtcNow}";
            Log.Information(formattedMessage);
        }

        public static Task LogCommands(Optional<CommandInfo> command, ICommandContext context, IResult result)
        {
            var commandName = command.IsSpecified ? command.Value.Name : "Unspecified Command";
            string formattedMessage = $"Discord:\t{commandName} was executed at {DateTime.UtcNow}";
            if (result.IsSuccess)
            {
                Log.Information(formattedMessage);
            }
            else
            {
                Log.Error($"{formattedMessage}\n\t\t\tInput: {context.Message}");
                Log.Error($"{result.Error}, {result.ErrorReason}");
            }
            return Task.CompletedTask;
        }
    }
}
