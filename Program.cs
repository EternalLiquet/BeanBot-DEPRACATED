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
        private CommandHandler _commandHandler;

        static void Main(string[] args)
            => new Program().StartAsync().GetAwaiter().GetResult();

        public async Task StartAsync()
        {
            Support.StartupOperations();
            await LogIntoDiscord();
            await InstantiateCommandServices();
            _discordClient.Log += LogHandler.LogMessages;
            await Task.Delay(-1);
        }

        private async Task InstantiateCommandServices()
        {
            Log.Information("Instantiating Command Services");
            CreateCommandServiceWithOptions(ref _commandService);
            _commandHandler = new CommandHandler(_discordClient, _commandService);
            await _commandHandler.InitializeCommandsAsync();
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
    }
}
