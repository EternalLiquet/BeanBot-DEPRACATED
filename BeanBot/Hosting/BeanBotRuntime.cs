using BeanBot.EventHandlers;
using BeanBot.Services;
using BeanBot.Util;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Hosting;

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
    private readonly LogHandler _logHandler;
    private readonly ILogger<BeanBotRuntime> _logger;

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
        DiscordPaginatorService paginatorService,
        LogHandler logHandler,
        ILogger<BeanBotRuntime> logger)
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
        _logHandler = logHandler ?? throw new ArgumentNullException(nameof(logHandler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        _discordClient.Log -= _logHandler.LogMessages;
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

    public async Task StopBackgroundServicesAsync(CancellationToken cancellationToken)
    {
        await _punHandler.DisposeAsync();
        await _healthCheckServer.StopAsync(cancellationToken);
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

    private async Task<bool> RunBoundedShutdownOperationAsync(
        Func<Task> beginOperation,
        string operationName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            BeanBotLog.DiscordShutdownOperationSkipped(_logger, operationName);
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
                    completedTask => BeanBotLog.DiscordShutdownLateFailure(
                        _logger,
                        operationName,
                        completedTask.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            BeanBotLog.DiscordShutdownOperationFailed(_logger, operationName, exception);
            return false;
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
