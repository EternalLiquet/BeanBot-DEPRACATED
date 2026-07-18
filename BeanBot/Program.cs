using BeanBot.Configuration;
using BeanBot.EventHandlers;
using BeanBot.Repository;
using BeanBot.Services;
using BeanBot.Util;

using Discord;
using Discord.Commands;
using Discord.WebSocket;

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
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
        private BeanBotOptions _options;
        private ServiceProvider _services;
        private DiscordOwnerErrorNotifier _ownerErrorNotifier;

        static void Main(string[] args)
        {
            var program = new Program();
            try
            {
                program.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Log.Fatal(exception, "BeanBot terminated because of an unhandled exception");
                Environment.ExitCode = 1;
            }
            finally
            {
                program.DisposeOwnerErrorNotifierAsync().GetAwaiter().GetResult();
                Log.CloseAndFlush();
            }
        }

        public async Task StartAsync()
        {
            var database = InitializeApplication();
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
            InitializeDiscordLifecycleTracking();
            CreateCommandServiceWithOptions();
            _services = CreateServiceProvider(database);
            _healthCheckServer = HealthCheckServer.Create(_options.HealthCheck, _discordClient, _discordConnectionHealth);
            _healthCheckServer?.Start();

            await LogIntoDiscord();
            await InstantiateCommandServices();
            _discordClient.Log += LogHandler.LogMessages;
            _autoPunPoster = new PunHandler(_discordClient, _options);
            _autoPunPoster.Start();
            _editMessageHandler = new EditMessageHandler(_discordClient);
            _editMessageHandler.InitializeEventListener();
            _newMemberHandler = new NewMemberHandler(_discordClient);
            _newMemberHandler.InitializeNewMembers();
            _reactHandler = new ReactHandler(
                _discordClient,
                _services.GetRequiredService<RoleReactService>());
            _reactHandler.InitializeReactDependentServices();

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelKeyPressHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };
            EventHandler processExitHandler = (_, __) => cts.Cancel();
            Console.CancelKeyPress += cancelKeyPressHandler;
            AppDomain.CurrentDomain.ProcessExit += processExitHandler;
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
                Console.CancelKeyPress -= cancelKeyPressHandler;
                AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
                _reactHandler?.Dispose();
                _newMemberHandler?.Dispose();
                _editMessageHandler?.Dispose();
                _commandHandler?.Dispose();
                _services?.Dispose();
                _discordClient.Log -= LogHandler.LogMessages;
                _discordClient.Ready -= OnDiscordReadyAsync;
                _discordClient.Disconnected -= OnDiscordDisconnectedAsync;

                if (_autoPunPoster is not null)
                {
                    await _autoPunPoster.DisposeAsync();
                }

                if (_healthCheckServer is not null)
                {
                    await _healthCheckServer.DisposeAsync();
                }

                await _ownerErrorNotifier.FlushAsync(TimeSpan.FromSeconds(3));
                try
                {
                    await _discordClient.StopAsync();
                    await _discordClient.LogoutAsync();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error shutting down: ");
                }

                await _ownerErrorNotifier.FlushAsync(TimeSpan.FromSeconds(3));
                _discordClient.Dispose();
                AppDomain.CurrentDomain.UnhandledException -= HandleUnhandledException;
                TaskScheduler.UnobservedTaskException -= HandleUnobservedTaskException;
            }
        }

        private async Task InstantiateCommandServices()
        {
            Log.Information("Instantiating Command Services");
            _commandHandler = new CommandHandler(_discordClient, _commandService, _services);
            await _commandHandler.InitializeCommandsAsync();
        }

        private IMongoDatabase InitializeApplication()
        {
            DirectorySetup.MakeSureAllDirectoriesExist();
            _discordConnectionHealth = new DiscordConnectionHealth();
            CreateNewDiscordSocketClientWithConfigurations();
            _ownerErrorNotifier = new DiscordOwnerErrorNotifier(_discordClient);
            LogHandler.CreateLoggerConfiguration(_ownerErrorNotifier);
            _options = BeanBotOptionsLoader.LoadFromEnvironment();

            Log.Information("Configuring MongoDB client");
            var mongoClient = new MongoClient(_options.MongoConnectionString);
            return mongoClient.GetDatabase("BeanBotDB");
        }

        private async Task DisposeOwnerErrorNotifierAsync()
        {
            if (_ownerErrorNotifier is not null)
            {
                await _ownerErrorNotifier.DisposeAsync();
            }
        }

        private ServiceProvider CreateServiceProvider(IMongoDatabase database)
        {
            return new ServiceCollection()
                .AddSingleton(_options)
                .AddSingleton(_discordClient)
                .AddSingleton(_commandService)
                .AddSingleton(database)
                .AddSingleton<FortuneAnswerQueue>()
                .AddSingleton(new Discord.Addons.Interactive.InteractiveService(_discordClient))
                .AddSingleton<RoleReactRepository>()
                .AddSingleton<RoleReactService>()
                .AddSingleton<DiscordMessageCleanupService>()
                .BuildServiceProvider();
        }

        private void CreateCommandServiceWithOptions()
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
                    await _discordClient.LoginAsync(TokenType.Bot, _options.BotToken);
                    await _discordClient.StartAsync();
                    await _discordClient.SetGameAsync("My purpose is to bully Hatate and succ the world dry", null, ActivityType.Playing);
                    loggedIn = true;
                }
                catch (Discord.Net.HttpException e)
                {
                    if (e.HttpCode == HttpStatusCode.Unauthorized)
                    {
                        Log.Fatal(e, "Discord rejected the configured bot token. Update BEANBOT_BOT_TOKEN and restart the process.");
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
                AlwaysDownloadUsers = false
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

        private static void HandleUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                Log.Fatal(exception, "An unhandled application exception occurred");
            }
            else
            {
                Log.Fatal("An unhandled non-Exception error occurred: {Error}", eventArgs.ExceptionObject);
            }
        }

        private static void HandleUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs eventArgs)
        {
            Log.Error(eventArgs.Exception, "An unobserved task exception occurred");
            eventArgs.SetObserved();
        }
    }
}
