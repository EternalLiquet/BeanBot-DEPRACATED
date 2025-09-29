using BeanBot.EventHandlers;
using BeanBot.Util;

using Discord;
using Discord.Commands;
using Discord.WebSocket;

using Serilog;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot
{
    class Program
    {
        private DiscordSocketClient _discordClient;
        private CommandService _commandService;
        private CommandHandler _commandHandler;
        private NewMemberHandler _newMemberHandler;
        private PunHandler _autoPunPoster;
        private EditMessageHandler _editMessageHandler;
        private ReactHandler _reactHandler;
        public static string queueEightBallAnswer;
        public static ulong queueRecipient;

        static void Main(string[] args)
            => new Program().StartAsync().GetAwaiter().GetResult();

        public async Task StartAsync()
        {
            Support.StartupOperations();
            await LogIntoDiscord();
            await InstantiateCommandServices();
            _discordClient.Log += LogHandler.LogMessages;
            _autoPunPoster = new PunHandler(_discordClient);
            _autoPunPoster.Start();
            _editMessageHandler = new EditMessageHandler(_discordClient);
            _editMessageHandler.InitializeEventListener();
            _newMemberHandler = new NewMemberHandler(_discordClient);
            _newMemberHandler.InitializeNewMembers();
            _reactHandler = new ReactHandler(_discordClient, new Services.RoleReactService(new Repository.RoleReactRepository()));
            await _reactHandler.InitializeReactDependentServices();

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, __) => cts.Cancel();
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Shutting Down");
            }
            finally
            {
                if (_autoPunPoster is not null)
                {
                    await _autoPunPoster.DisposeAsync();
                }

                try
                {
                    await _discordClient.StopAsync();
                    await _discordClient.LogoutAsync();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error shutting down: ");

                }
            }
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
                CaseSensitiveCommands = false,
            });
        }

        private async Task LogIntoDiscord()
        {
            CreateNewDiscordSocketClientWithConfigurations();
            bool loggedIn = false;
            while (loggedIn == false)
            {
                try
                {
                    await _discordClient.LoginAsync(TokenType.Bot, AppSettings.Settings["botToken"]);
                    await _discordClient.StartAsync();
                    await _discordClient.SetGameAsync("My purpose is to bully Hatate and succ the world dry", null, ActivityType.Playing);
                    _discordClient.Ready += () =>
                    {
                        Log.Information("Bean Bot successfully connected");
                        return Task.CompletedTask;
                    };
                    loggedIn = true;
                }
                catch (Discord.Net.HttpException e)
                {
                    Log.Error(e.ToString());
                    Log.Error($"Bot Token was incorrect, please review the settings file in {Path.GetFullPath(AppSettings.settingsFilePath)}");
                    if (e.HttpCode == HttpStatusCode.Unauthorized)
                    {
                        AppSettings.FixToken();
                    }
                }
            }
        }

        private void CreateNewDiscordSocketClientWithConfigurations()
        {
            _discordClient = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Verbose,
                MessageCacheSize = 50,
                ExclusiveBulkDelete = true,
                AlwaysDownloadUsers = true
            });
        }
    }
}
