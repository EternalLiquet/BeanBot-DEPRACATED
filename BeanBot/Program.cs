using BeanBot.EventHandlers;
using BeanBot.Services;
using BeanBot.Util;

using Discord;
using Discord.Commands;
using Discord.WebSocket;

using Serilog;

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot
{
    class Program
    {
        private DiscordSocketClient _discordClient;
        private DiscordConnectionHealth _discordConnectionHealth;
        private CommandService _commandService;
        private CommandHandler _commandHandler;
        private NewMemberHandler _newMemberHandler;
        private PunHandler _autoPunPoster;
        private EditMessageHandler _editMessageHandler;
        private ReactHandler _reactHandler;
        private HealthCheckServer _healthCheckServer;
        public static string queueEightBallAnswer;
        public static ulong queueRecipient;

        static void Main(string[] args)
            => new Program().StartAsync().GetAwaiter().GetResult();

        public async Task StartAsync()
        {
            Support.StartupOperations();
            _discordConnectionHealth = new DiscordConnectionHealth();
            CreateNewDiscordSocketClientWithConfigurations();
            InitializeDiscordLifecycleTracking();
            _healthCheckServer = HealthCheckServer.CreateFromSettings(_discordClient, _discordConnectionHealth);
            _healthCheckServer?.Start();

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

                if (_healthCheckServer is not null)
                {
                    await _healthCheckServer.DisposeAsync();
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
            bool loggedIn = false;
            while (loggedIn == false)
            {
                try
                {
                    await _discordClient.LoginAsync(TokenType.Bot, AppSettings.Settings["botToken"]);
                    await _discordClient.StartAsync();
                    await _discordClient.SetGameAsync("My purpose is to bully Hatate and succ the world dry", null, ActivityType.Playing);
                    loggedIn = true;
                }
                catch (Discord.Net.HttpException e)
                {
                    if (e.HttpCode == HttpStatusCode.Unauthorized)
                    {
                        Log.Fatal(e, "Discord rejected the configured bot token. Update {SettingName} and restart the process.", AppSettings.DescribeSetting("botToken"));
                        throw;
                    }

                    Log.Error(e, "Discord login failed; retrying in 30 seconds");
                    await Task.Delay(TimeSpan.FromSeconds(30));
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

        private void InitializeDiscordLifecycleTracking()
        {
            _discordClient.Ready += OnDiscordReadyAsync;
            _discordClient.Disconnected += OnDiscordDisconnectedAsync;
        }

        private Task OnDiscordReadyAsync()
        {
            _discordConnectionHealth.MarkReady();
            Log.Information("Bean Bot successfully connected");
            return Task.CompletedTask;
        }

        private Task OnDiscordDisconnectedAsync(Exception exception)
        {
            _discordConnectionHealth.MarkDisconnected(exception);
            if (exception is null)
            {
                Log.Warning("Bean Bot disconnected from Discord");
            }
            else
            {
                Log.Warning(exception, "Bean Bot disconnected from Discord");
            }

            return Task.CompletedTask;
        }
    }
}
