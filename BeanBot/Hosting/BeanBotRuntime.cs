using BeanBot.Discord.Events;
using BeanBot.Discord.Lifecycle;
using BeanBot.Discord.Messaging;
using BeanBot.Health;
using BeanBot.Logging;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Hosting;

internal sealed class BeanBotRuntime : IBeanBotRuntime
{
    private readonly DiscordSocketClient _discordClient;
    private readonly DiscordConnectionHealth _discordConnectionHealth;
    private readonly DiscordGatewayRecoveryService _discordGatewayRecovery;
    private readonly DiscordOutageRecoveryNotifier _discordOutageRecoveryNotifier;
    private readonly DiscordLifecycleCoordinator _discordLifecycleCoordinator;
    private readonly DiscordStartupService _discordStartupService;
    private readonly DiscordOwnerErrorNotifier _ownerErrorNotifier;
    private readonly HealthCheckServer _healthCheckServer;
    private readonly CommandHandler _commandHandler;
    private readonly PunHandler _punHandler;
    private readonly EditMessageHandler _editMessageHandler;
    private readonly NewMemberHandler _newMemberHandler;
    private readonly NewMemberWelcomeService _newMemberWelcomeService;
    private readonly ReactHandler _reactHandler;
    private readonly DiscordMessageWaiter _messageWaiter;
    private readonly DiscordPaginatorService _paginatorService;
    private readonly LogHandler _logHandler;
    private readonly ILogger<BeanBotRuntime> _logger;
    private bool _canDisposeDiscordClient;

    public BeanBotRuntime(
        DiscordSocketClient discordClient,
        DiscordConnectionHealth discordConnectionHealth,
        DiscordGatewayRecoveryService discordGatewayRecovery,
        DiscordOutageRecoveryNotifier discordOutageRecoveryNotifier,
        DiscordLifecycleCoordinator discordLifecycleCoordinator,
        DiscordStartupService discordStartupService,
        DiscordOwnerErrorNotifier ownerErrorNotifier,
        HealthCheckServer healthCheckServer,
        CommandHandler commandHandler,
        PunHandler punHandler,
        EditMessageHandler editMessageHandler,
        NewMemberHandler newMemberHandler,
        NewMemberWelcomeService newMemberWelcomeService,
        ReactHandler reactHandler,
        DiscordMessageWaiter messageWaiter,
        DiscordPaginatorService paginatorService,
        LogHandler logHandler,
        ILogger<BeanBotRuntime> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _discordConnectionHealth = discordConnectionHealth ?? throw new ArgumentNullException(nameof(discordConnectionHealth));
        _discordGatewayRecovery = discordGatewayRecovery ?? throw new ArgumentNullException(nameof(discordGatewayRecovery));
        _discordOutageRecoveryNotifier = discordOutageRecoveryNotifier ?? throw new ArgumentNullException(nameof(discordOutageRecoveryNotifier));
        _discordLifecycleCoordinator = discordLifecycleCoordinator ?? throw new ArgumentNullException(nameof(discordLifecycleCoordinator));
        _discordStartupService = discordStartupService ?? throw new ArgumentNullException(nameof(discordStartupService));
        _ownerErrorNotifier = ownerErrorNotifier ?? throw new ArgumentNullException(nameof(ownerErrorNotifier));
        _healthCheckServer = healthCheckServer ?? throw new ArgumentNullException(nameof(healthCheckServer));
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        _punHandler = punHandler ?? throw new ArgumentNullException(nameof(punHandler));
        _editMessageHandler = editMessageHandler ?? throw new ArgumentNullException(nameof(editMessageHandler));
        _newMemberHandler = newMemberHandler ?? throw new ArgumentNullException(nameof(newMemberHandler));
        _newMemberWelcomeService = newMemberWelcomeService ?? throw new ArgumentNullException(nameof(newMemberWelcomeService));
        _reactHandler = reactHandler ?? throw new ArgumentNullException(nameof(reactHandler));
        _messageWaiter = messageWaiter ?? throw new ArgumentNullException(nameof(messageWaiter));
        _paginatorService = paginatorService ?? throw new ArgumentNullException(nameof(paginatorService));
        _logHandler = logHandler ?? throw new ArgumentNullException(nameof(logHandler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool HasActiveDiscordLifecycleOperation
        => _discordLifecycleCoordinator.HasActiveSequence ||
           _newMemberWelcomeService.HasActiveDiscordOperation;

    public bool CanDisposeDiscordClient => _canDisposeDiscordClient;

    public void SubscribeApplicationEvents()
    {
        AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
        TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
        _discordClient.Ready += OnDiscordReadyAsync;
        _discordClient.Disconnected += OnDiscordDisconnectedAsync;
    }

    public Task StartHealthServerAsync(CancellationToken cancellationToken)
        => _healthCheckServer.StartAsync(cancellationToken);

    public Task StartDiscordAsync(CancellationToken cancellationToken)
        => _discordStartupService.StartAsync(cancellationToken);

    public void StartGatewayRecovery() => _discordGatewayRecovery.StartMonitoring();

    public async Task StartCommandServicesAsync()
    {
        BeanBotLog.CommandServicesCreated(_logger);
        await _commandHandler.InitializeCommandsAsync();
    }

    public void StartEventAndBackgroundServices()
    {
        _discordClient.Log += _logHandler.LogMessages;
        _punHandler.Start();
        _editMessageHandler.InitializeEventListener();
        _newMemberWelcomeService.Start();
        _newMemberHandler.InitializeNewMembers();
        _reactHandler.InitializeReactDependentServices();
    }

    public void StopReactionServices() => _reactHandler.Dispose();

    public void StopNewMemberEvents()
    {
        _newMemberHandler.Dispose();
        _newMemberWelcomeService.StopAccepting();
        _newMemberWelcomeService.StopAsync().GetAwaiter().GetResult();
    }

    public void StopEditedMessageEvents() => _editMessageHandler.Dispose();

    public void StopCommandServices() => _commandHandler.Dispose();

    public void StopMessageWaiter() => _messageWaiter.Dispose();

    public void StopPaginator() => _paginatorService.Dispose();

    public void UnsubscribeDiscordLog() => _discordClient.Log -= _logHandler.LogMessages;

    public Task StopGatewayRecoveryAsync()
        => _discordGatewayRecovery.DisposeAsync().AsTask();

    public void UnsubscribeApplicationEvents()
    {
        _discordClient.Ready -= OnDiscordReadyAsync;
        _discordClient.Disconnected -= OnDiscordDisconnectedAsync;
        AppDomain.CurrentDomain.UnhandledException -= HandleUnhandledException;
        TaskScheduler.UnobservedTaskException -= HandleUnobservedTaskException;
    }

    public Task StopPunServiceAsync() => _punHandler.DisposeAsync().AsTask();

    public Task StopHealthServerAsync(CancellationToken cancellationToken)
        => _healthCheckServer.StopAsync(cancellationToken);

    public Task FlushOwnerAlertsAsync()
        => _ownerErrorNotifier.FlushAsync(TimeSpan.FromSeconds(3));

    public async Task StopDiscordAsync(CancellationToken cancellationToken)
    {
        var operationTimeout = DiscordGatewayRecoveryOptions.Default.LifecycleOperationTimeout;
        _canDisposeDiscordClient = false;
        var outcome = await _discordLifecycleCoordinator.RunSequenceAsync(
            "application-shutdown",
            [
                new("stop", _discordClient.StopAsync),
                new("logout", _discordClient.LogoutAsync)
            ],
            operationTimeout,
            cancellationToken);
        _canDisposeDiscordClient = outcome.IsCompleted;
        if (!outcome.IsCompleted)
        {
            var exception = outcome.Exception ?? new InvalidOperationException(
                "Discord application shutdown could not acquire lifecycle ownership.");
            BeanBotLog.DiscordShutdownOperationFailed(
                _logger,
                outcome.Operation ?? outcome.Sequence,
                exception);
            throw exception;
        }
    }

    public void DisposeDiscordClient() => _discordClient.Dispose();

    internal static async Task RunBoundedShutdownOperationAsync(
        Func<Task> beginOperation,
        string operationName,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            BeanBotLog.DiscordShutdownOperationSkipped(logger, operationName);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var operation = beginOperation();
        try
        {
            await operation.WaitAsync(timeout, cancellationToken);
        }
        catch (Exception exception)
        {
            if (!operation.IsCompleted)
            {
                _ = operation.ContinueWith(
                    completedTask => BeanBotLog.DiscordShutdownLateFailure(
                        logger,
                        operationName,
                        completedTask.Exception!),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            BeanBotLog.DiscordShutdownOperationFailed(logger, operationName, exception);
            throw;
        }
    }

    private async Task OnDiscordReadyAsync()
    {
        _discordConnectionHealth.MarkReady();
        _discordGatewayRecovery.NotifyReady();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var loginState = _discordClient.LoginState;
            var connectionState = _discordClient.ConnectionState;
            BeanBotLog.DiscordReady(
                _logger,
                loginState,
                connectionState);
        }
        try
        {
            await _discordOutageRecoveryNotifier.NotifyIfOutageRecoveredAsync(DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            BeanBotLog.OutageRecoveryProcessingFailed(_logger, exception);
        }
    }

    private Task OnDiscordDisconnectedAsync(Exception? exception)
    {
        _discordConnectionHealth.MarkDisconnected(exception);
        var snapshot = _discordConnectionHealth.CreateSnapshot(_discordClient);
        if (exception is null)
        {
            BeanBotLog.DiscordDisconnected(
                _logger,
                snapshot.LoginState,
                snapshot.ConnectionState,
                snapshot.MostRecentDisconnectReason);
        }
        else
        {
            BeanBotLog.DiscordDisconnected(
                _logger,
                snapshot.LoginState,
                snapshot.ConnectionState,
                snapshot.MostRecentDisconnectReason,
                exception);
        }

        _discordGatewayRecovery.StartMonitoring();
        return Task.CompletedTask;
    }

    private void HandleUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            BeanBotLog.UnhandledApplicationException(_logger, exception);
        }
        else
        {
            BeanBotLog.UnhandledApplicationError(_logger, eventArgs.ExceptionObject);
        }
    }

    private void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        BeanBotLog.UnobservedTaskException(_logger, eventArgs.Exception);
        eventArgs.SetObserved();
    }
}
