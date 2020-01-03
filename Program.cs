using BeanBot.EventHandlers;
using BeanBot.Util;

using Discord;
using Discord.Commands;
using Discord.WebSocket;

using Serilog;

using System.IO;
using System.Threading.Tasks;

namespace BeanBot
{
    class Program
    {
        private DiscordSocketClient _discordClient;
        private CommandService _commandService;

        static void Main(string[] args)
            => new Program().StartAsync().GetAwaiter().GetResult();

        public async Task StartAsync()
        {
            Support.StartupOperations();
            await LogIntoDiscord();
            CreateCommandServiceWithOptions(ref _commandService);
            _discordClient.Log += LogMessages;
            _commandService.Log += LogMessages;
            await Task.Delay(-1);
        }

        private void CreateCommandServiceWithOptions(ref CommandService _commandService)
        {
            _commandService = new CommandService(new CommandServiceConfig
            {
                LogLevel = LogSeverity.Verbose,
                CaseSensitiveCommands = false
            });
        }

        private async Task LogIntoDiscord()
        {
            CreateNewDiscordSocketClientWithConfigurations();
            try
            {
                await _discordClient.LoginAsync(TokenType.Bot, Support.BotToken);
                await _discordClient.StartAsync();
                _discordClient.Ready += () =>
                {
                    Log.Information("Bean Bot successfully connected");
                    return Task.CompletedTask;
                };
            }
            catch (Discord.Net.HttpException e)
            {
                Log.Error(e.ToString());
                Log.Error($"Bean Token was incorrect, please review the bean token file in {Path.GetFullPath(TokenSetup.botTokenFilePath)}");
            }
        }

        private void CreateNewDiscordSocketClientWithConfigurations()
        {
            _discordClient = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Verbose,
                MessageCacheSize = 50
            });
        }

        private Task LogMessages(LogMessage messages)
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
    }
}
