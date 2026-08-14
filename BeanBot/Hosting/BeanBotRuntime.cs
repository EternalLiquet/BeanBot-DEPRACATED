using BeanBot.EventHandlers;
using BeanBot.Services;
using BeanBot.Util;

using Discord.WebSocket;

using Serilog;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.Hosting
{
    internal sealed class BeanBotRuntime : IBeanBotRuntime
    {
        private readonly DiscordSocketClient _discordClient;
        private readonly DiscordConnectionHealth _discordConnectionHealth;
        private readonly DiscordGatewayRecoveryService _discordGatewayRecovery;
        private readonly DiscordOutageRecoveryNotifier _discordOutageRecoveryNotifier;
        private readonly DiscordStartupLifecycle _discordStartupLifecycle;
        private readonly DiscordStartupService _discordStartupService;
        private readonly DiscordOwnerErrorNotifier _ownerErrorNotifier;
        private readonly HealthCheckServer _healthCheckServer;
        private readonly CommandHandler _commandHandler;
        private readonly PunHandler _punHandler;
        private readonly EditMessageHandler _editMessageHandler;
        private readonly NewMemberHandler _newMemberHandler;
        private readonly ReactHandler _reactHandler;
        private readonly DiscordMessageWaiter _messageWaiter;
        private readonly DiscordPaginatorService _paginatorService;

        public BeanBotRuntime(
            DiscordSocketClient discordClient,
            DiscordConnectionHealth discordConnectionHealth,
            DiscordGatewayRecoveryService discordGatewayRecovery,
            DiscordOutageRecoveryNotifier discordOutageRecoveryNotifier,
            DiscordStartupLifecycle discordStartupLifecycle,
            DiscordStartupService discordStartupService,
            DiscordOwnerErrorNotifier ownerErrorNotifier,
            HealthCheckServer healthCheckServer,
            CommandHandler commandHandler,
            PunHandler punHandler,
            EditMessageHandler editMessageHandler,
            NewMemberHandler newMemberHandler,
            ReactHandler reactHandler,
            DiscordMessageWaiter messageWaiter,
            DiscordPaginatorService paginatorService)
        {
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
            _discordConnectionHealth = discordConnectionHealth ?? throw new ArgumentNullException(nameof(discordConnectionHealth));
            _discordGatewayRecovery = discordGatewayRecovery ?? throw new ArgumentNullException(nameof(discordGatewayRecovery));
            _discordOutageRecoveryNotifier = discordOutageRecoveryNotifier ?? throw new ArgumentNullException(nameof(discordOutageRecoveryNotifier));
            _discordStartupLifecycle = discordStartupLifecycle ?? throw new ArgumentNullException(nameof(discordStartupLifecycle));
            _discordStartupService = discordStartupService ?? throw new ArgumentNullException(nameof(discordStartupService));
            _ownerErrorNotifier = ownerErrorNotifier ?? throw new ArgumentNullException(nameof(ownerErrorNotifier));
            _healthCheckServer = healthCheckServer ?? throw new ArgumentNullException(nameof(healthCheckServer));
            _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
            _punHandler = punHandler ?? throw new ArgumentNullException(nameof(punHandler));
            _editMessageHandler = editMessageHandler ?? throw new ArgumentNullException(nameof(editMessageHandler));
            _newMemberHandler = newMemberHandler ?? throw new ArgumentNullException(nameof(newMemberHandler));
            _reactHandler = reactHandler ?? throw new ArgumentNullException(nameof(reactHandler));
            _messageWaiter = messageWaiter ?? throw new ArgumentNullException(nameof(messageWaiter));
            _paginatorService = paginatorService ?? throw new ArgumentNullException(nameof(paginatorService));
        }

        public bool HasUnfinishedDiscordStartupOperation
            => _discordStartupLifecycle.HasUnfinishedOperation;

        public void SubscribeApplicationEvents()
        {
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
            _discordClient.Ready += OnDiscordReadyAsync;
            _discordClient.Disconnected += OnDiscordDisconnectedAsync;
        }

        public void StartHealthServer() => _healthCheckServer.Start();

        public Task StartDiscordAsync(CancellationToken cancellationToken)
            => _discordStartupService.StartAsync(cancellationToken);

        public void StartGatewayRecovery() => _discordGatewayRecovery.StartMonitoring();

        public async Task StartCommandServicesAsync()
        {
            Log.Information("Instantiating Command Services");
            await _commandHandler.InitializeCommandsAsync();
        }

        public void StartEventAndBackgroundServices()
        {
            _discordClient.Log += LogHandler.LogMessages;
            _punHandler.Start();
            _editMessageHandler.InitializeEventListener();
            _newMemberHandler.InitializeNewMembers();
            _reactHandler.InitializeReactDependentServices();
        }

        public void StopEventAndCommandServices()
        {
            _reactHandler.Dispose();
            _newMemberHandler.Dispose();
            _editMessageHandler.Dispose();
            _commandHandler.Dispose();
            _messageWaiter.Dispose();
            _paginatorService.Dispose();
            _discordClient.Log -= LogHandler.LogMessages;
        }

        public Task StopGatewayRecoveryAsync()
            => _discordGatewayRecovery.DisposeAsync().AsTask();

        public void UnsubscribeApplicationEvents()
        {
            _discordClient.Ready -= OnDiscordReadyAsync;
            _discordClient.Disconnected -= OnDiscordDisconnectedAsync;
            AppDomain.CurrentDomain.UnhandledException -= HandleUnhandledException;
            TaskScheduler.UnobservedTaskException -= HandleUnobservedTaskException;
        }

        public async Task StopBackgroundServicesAsync()
        {
            await _punHandler.DisposeAsync();
            await _healthCheckServer.DisposeAsync();
        }

        public Task FlushOwnerAlertsAsync()
            => _ownerErrorNotifier.FlushAsync(TimeSpan.FromSeconds(3));

        public async Task StopDiscordAsync(CancellationToken cancellationToken)
        {
            var operationTimeout = DiscordGatewayRecoveryOptions.Default.LifecycleOperationTimeout;
            if (!await RunBoundedShutdownOperationAsync(
                _discordClient.StopAsync,
                "stop",
                operationTimeout,
                cancellationToken))
            {
                return;
            }

            await RunBoundedShutdownOperationAsync(
                _discordClient.LogoutAsync,
                "logout",
                operationTimeout,
                cancellationToken);
        }

        public void DisposeDiscordClient() => _discordClient.Dispose();

        private static async Task<bool> RunBoundedShutdownOperationAsync(
            Func<Task> beginOperation,
            string operationName,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Warning(
                    "Skipping Discord {Operation} operation because the host shutdown deadline elapsed",
                    operationName);
                return false;
            }

            var operation = beginOperation();
            try
            {
                await operation.WaitAsync(timeout, cancellationToken);
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
