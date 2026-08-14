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
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot
{
    class Program
    {
        private DiscordSocketClient _discordClient;
        private DiscordConnectionHealth _discordConnectionHealth;
        private DiscordGatewayRecoveryService _discordGatewayRecovery;
        private DiscordOutageStore _discordOutageStore;
        private DiscordOutageRecoveryNotifier _discordOutageRecoveryNotifier;
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
        private DiscordStartupLifecycle _discordStartupLifecycle;

        static void Main(string[] args)
        {
            var program = new Program();
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
                program.StartAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Log.Warning("Shutting down");
            }
            catch (Exception exception)
            {
                Log.Fatal(exception, "BeanBot terminated because of an unhandled exception");
                Environment.ExitCode = 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancelKeyPressHandler;
                AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
                program.DisposeOwnerErrorNotifierAsync().GetAwaiter().GetResult();
                Log.CloseAndFlush();
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            var database = InitializeApplication();
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
            InitializeDiscordLifecycleTracking();
            CreateCommandServiceWithOptions();
            _services = CreateServiceProvider(database);
            _healthCheckServer = HealthCheckServer.Create(_options.HealthCheck, _discordClient, _discordConnectionHealth);
            try
            {
                _healthCheckServer?.Start();
                var startupOptions = DiscordStartupOptions.Default;
                _discordStartupLifecycle = new DiscordStartupLifecycle(
                    _discordClient,
                    _options.BotToken,
                    startupOptions.LifecycleOperationTimeout);
                var startupService = new DiscordStartupService(_discordStartupLifecycle, startupOptions);
                await startupService.StartAsync(cancellationToken);
                _discordGatewayRecovery.StartMonitoring();
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

                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                _reactHandler?.Dispose();
                _newMemberHandler?.Dispose();
                _editMessageHandler?.Dispose();
                _commandHandler?.Dispose();
                _services?.Dispose();
                _discordClient.Log -= LogHandler.LogMessages;

                if (_discordGatewayRecovery is not null)
                {
                    await _discordGatewayRecovery.DisposeAsync();
                }

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
                await StopDiscordAsync();

                await _ownerErrorNotifier.FlushAsync(TimeSpan.FromSeconds(3));
                if (_discordStartupLifecycle?.HasUnfinishedOperation != true)
                {
                    _discordClient.Dispose();
                }
                else
                {
                    Log.Warning(
                        "Skipping Discord client disposal because a startup lifecycle operation is still running; process exit will reclaim it");
                }
                _discordOutageRecoveryNotifier?.Dispose();
                _discordOutageStore?.Dispose();
                AppDomain.CurrentDomain.UnhandledException -= HandleUnhandledException;
                TaskScheduler.UnobservedTaskException -= HandleUnobservedTaskException;
            }
        }

        private async Task StopDiscordAsync()
        {
            if (_discordStartupLifecycle?.HasUnfinishedOperation == true)
            {
                Log.Warning(
                    "Skipping Discord stop/logout because a startup lifecycle operation is still running; process exit will reclaim it");
                return;
            }

            var operationTimeout = DiscordGatewayRecoveryOptions.Default.LifecycleOperationTimeout;
            if (!await RunBoundedShutdownOperationAsync(
                _discordClient.StopAsync(),
                "stop",
                operationTimeout))
            {
                return;
            }

            await RunBoundedShutdownOperationAsync(
                _discordClient.LogoutAsync(),
                "logout",
                operationTimeout);
        }

        private static async Task<bool> RunBoundedShutdownOperationAsync(
            Task operation,
            string operationName,
            TimeSpan timeout)
        {
            try
            {
                await operation.WaitAsync(timeout);
                return true;
            }
            catch (Exception exception)
            {
                if (!operation.IsCompleted)
                {
                    _ = operation.ContinueWith(
                        completedTask => Log.Error(
                            completedTask.Exception,
                            "Discord {Operation} operation failed after its shutdown wait ended",
                            operationName),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                Log.Error(
                    exception,
                    "Discord {Operation} operation did not complete during bounded shutdown",
                    operationName);
                return false;
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
            var ownerAlertDelivery = new DiscordOwnerAlertDelivery(_discordClient);
            _ownerErrorNotifier = new DiscordOwnerErrorNotifier(ownerAlertDelivery);
            LogHandler.CreateLoggerConfiguration(_ownerErrorNotifier);
            _discordOutageStore = new DiscordOutageStore(
                Path.GetFullPath(DirectorySetup.botBaseDirectory));
            _discordOutageRecoveryNotifier = new DiscordOutageRecoveryNotifier(
                _discordOutageStore,
                ownerAlertDelivery);
            _options = BeanBotOptionsLoader.LoadFromEnvironment();
            var recoveryOptions = DiscordGatewayRecoveryOptions.Default;
            _discordGatewayRecovery = new DiscordGatewayRecoveryService(
                () => _discordConnectionHealth.CreateSnapshot(_discordClient),
                new DiscordGatewayLifecycle(
                    _discordClient,
                    _options.BotToken,
                    recoveryOptions.LifecycleOperationTimeout),
                _discordOutageStore,
                recoveryOptions);

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
                .AddSingleton<DiscordMessageWaiter>()
                .AddSingleton<DiscordPaginatorService>()
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

        private void CreateNewDiscordSocketClientWithConfigurations()
        {
            _discordClient = new DiscordSocketClient(CreateDiscordSocketConfig());
        }

        internal static DiscordSocketConfig CreateDiscordSocketConfig()
        {
            return new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Verbose,
                MessageCacheSize = 50,
                AlwaysDownloadUsers = false,
                GatewayIntents = GatewayIntents.Guilds
                    | GatewayIntents.GuildMembers
                    | GatewayIntents.GuildEmojis
                    | GatewayIntents.GuildMessages
                    | GatewayIntents.DirectMessages
                    | GatewayIntents.GuildMessageReactions
                    | GatewayIntents.DirectMessageReactions
                    | GatewayIntents.MessageContent
            };
        }

        private void InitializeDiscordLifecycleTracking()
        {
            _discordClient.Ready += OnDiscordReadyAsync;
            _discordClient.Disconnected += OnDiscordDisconnectedAsync;
        }

        private async Task OnDiscordReadyAsync()
        {
            _discordConnectionHealth.MarkReady();
            _discordGatewayRecovery.NotifyReady();
            Log.Information(
                "BeanBot Discord gateway reached Ready. LoginState={LoginState}, ConnectionState={ConnectionState}",
                _discordClient.LoginState,
                _discordClient.ConnectionState);
            try
            {
                await _discordOutageRecoveryNotifier.NotifyIfOutageRecoveredAsync(DateTimeOffset.UtcNow);
            }
            catch (Exception exception)
            {
                // Ready must remain a successful gateway event even if local persistence is unavailable.
                Log.Error(
                    exception,
                    "Discord reached Ready, but the persisted outage recovery notification could not be processed");
            }
        }

        private Task OnDiscordDisconnectedAsync(Exception exception)
        {
            _discordConnectionHealth.MarkDisconnected(exception);
            var snapshot = _discordConnectionHealth.CreateSnapshot(_discordClient);
            if (exception is null)
            {
                Log.Warning(
                    "BeanBot disconnected from Discord. LoginState={LoginState}, ConnectionState={ConnectionState}, MostRecentDisconnectReason={MostRecentDisconnectReason}",
                    snapshot.LoginState,
                    snapshot.ConnectionState,
                    snapshot.MostRecentDisconnectReason);
            }
            else
            {
                Log.Warning(
                    exception,
                    "BeanBot disconnected from Discord. LoginState={LoginState}, ConnectionState={ConnectionState}, MostRecentDisconnectReason={MostRecentDisconnectReason}",
                    snapshot.LoginState,
                    snapshot.ConnectionState,
                    snapshot.MostRecentDisconnectReason);
            }

            _discordGatewayRecovery.StartMonitoring();

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
