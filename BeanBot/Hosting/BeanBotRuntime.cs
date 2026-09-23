using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.Lifecycle;
using BeanBot.Discord.Messaging;
using BeanBot.Discord.Puns;
using BeanBot.Health;
using BeanBot.Logging;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeanBot.Hosting;

internal sealed class BeanBotRuntime : IBeanBotRuntime, IDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly DiscordConnectionHealth _discordConnectionHealth;
    private readonly DiscordGatewayRecoveryService _discordGatewayRecovery;
    private readonly DiscordOutageRecoveryNotifier _discordOutageRecoveryNotifier;
    private readonly DiscordLifecycleCoordinator _discordLifecycleCoordinator;
    private readonly DiscordStartupService _discordStartupService;
    private readonly DiscordOwnerErrorNotifier _ownerErrorNotifier;
    private readonly HealthCheckServer _healthCheckServer;
    private readonly BeanBotInstanceLease _instanceLease;
    private readonly CommandHandler _commandHandler;
    private readonly InteractionHandler[] _interactionHandlers;
    private readonly LegacyCommandReplySender _commandReplySender;
    private readonly DailyPunService _dailyPunService;
    private readonly FortuneMessageEditHandler _fortuneMessageEditHandler;
    private readonly NewMemberHandler _newMemberHandler;
    private readonly NewMemberWelcomeService _newMemberWelcomeService;
    private readonly ReactionRoleHandler _reactionRoleHandler;
    private readonly DiscordMessageWaiter _messageWaiter;
    private readonly DiscordPaginatorService _paginatorService;
    private readonly LogHandler _logHandler;
    private readonly ILogger<BeanBotRuntime> _logger;
    private readonly object _sideEffectStopSync = new();
    private readonly CancellationTokenSource _sideEffectCancellation = new();
    private readonly CancellationTokenRegistration _applicationStoppingRegistration;
    private Task? _gatewayRecoveryStopTask;
    private Task? _punStopTask;
    private Task? _ownerAlertStopTask;
    private int _ownedReadyOperationCount;
    private int _sideEffectAdmissionStopped;
    private int _disposed;
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
        BeanBotInstanceLease instanceLease,
        CommandHandler commandHandler,
        IEnumerable<InteractionHandler> interactionHandlers,
        LegacyCommandReplySender commandReplySender,
        DailyPunService dailyPunService,
        FortuneMessageEditHandler fortuneMessageEditHandler,
        NewMemberHandler newMemberHandler,
        NewMemberWelcomeService newMemberWelcomeService,
        ReactionRoleHandler reactionRoleHandler,
        DiscordMessageWaiter messageWaiter,
        DiscordPaginatorService paginatorService,
        LogHandler logHandler,
        IHostApplicationLifetime applicationLifetime,
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
        _instanceLease = instanceLease ?? throw new ArgumentNullException(nameof(instanceLease));
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        // The core host can be composed without interactions; every registered handler owns teardown safety.
        _interactionHandlers = interactionHandlers?.ToArray() ?? throw new ArgumentNullException(nameof(interactionHandlers));
        _commandReplySender = commandReplySender ?? throw new ArgumentNullException(nameof(commandReplySender));
        _dailyPunService = dailyPunService ?? throw new ArgumentNullException(nameof(dailyPunService));
        _fortuneMessageEditHandler = fortuneMessageEditHandler ?? throw new ArgumentNullException(nameof(fortuneMessageEditHandler));
        _newMemberHandler = newMemberHandler ?? throw new ArgumentNullException(nameof(newMemberHandler));
        _newMemberWelcomeService = newMemberWelcomeService ?? throw new ArgumentNullException(nameof(newMemberWelcomeService));
        _reactionRoleHandler = reactionRoleHandler ?? throw new ArgumentNullException(nameof(reactionRoleHandler));
        _messageWaiter = messageWaiter ?? throw new ArgumentNullException(nameof(messageWaiter));
        _paginatorService = paginatorService ?? throw new ArgumentNullException(nameof(paginatorService));
        _logHandler = logHandler ?? throw new ArgumentNullException(nameof(logHandler));
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _applicationStoppingRegistration = applicationLifetime.ApplicationStopping.Register(StopSideEffectAdmission);
    }

    public bool HasActiveDiscordLifecycleOperation
        => _discordLifecycleCoordinator.HasActiveSequence
            || _ownerErrorNotifier.HasActiveDiscordOperation
            || _discordOutageRecoveryNotifier.HasActiveDiscordOperation
            || Volatile.Read(ref _ownedReadyOperationCount) != 0
            || _dailyPunService.HasActiveDiscordOperation
            || _newMemberWelcomeService.HasActiveDiscordOperation
            || _fortuneMessageEditHandler.HasInFlightOperations
            || _commandReplySender.HasPendingOperations
            || _interactionHandlers.Any(handler => handler.HasPendingOperations)
            || _reactionRoleHandler.HasPendingOperations
            || _paginatorService.HasPendingOperations;

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

    public async Task AcquireInstanceLeaseAsync(CancellationToken cancellationToken)
    {
        var botUser = _discordClient.CurrentUser;
        if (botUser is null)
        {
            throw new InvalidOperationException(
                "Discord startup completed without an authenticated bot identity; active-instance ownership cannot be established.");
        }

        await _instanceLease.AcquireAsync(botUser.Id, cancellationToken);
        _ownerErrorNotifier.StartAccepting();

        // Ready can arrive during Discord startup before lease ownership exists. Keep
        // health observation active during startup, then replay only the side effects
        // that are safe for the confirmed owner.
        if (_discordConnectionHealth.CreateSnapshot(_discordClient).IsHealthy)
        {
            await RunTrackedOwnedDiscordReadyAsync();
        }
    }

    public void StartGatewayRecovery()
    {
        lock (_sideEffectStopSync)
        {
            if (!CanAdmitSideEffects())
            {
                return;
            }

            _discordGatewayRecovery.StartMonitoring();
        }
    }

    public async Task StartCommandServicesAsync()
    {
        lock (_sideEffectStopSync)
        {
            if (!CanAdmitSideEffects())
            {
                return;
            }
        }

        BeanBotLog.CommandServicesCreated(_logger);
        await _commandHandler.InitializeCommandsAsync();
    }

    public void StartEventAndBackgroundServices()
    {
        lock (_sideEffectStopSync)
        {
            if (!CanAdmitSideEffects())
            {
                return;
            }

            _discordClient.Log += _logHandler.LogMessages;
            _dailyPunService.Start();
            _fortuneMessageEditHandler.InitializeEventListener();
            _newMemberWelcomeService.Start();
            _newMemberHandler.InitializeNewMembers();
            _reactionRoleHandler.InitializeReactDependentServices();
        }
    }

    public void StopReactionServices() => _reactionRoleHandler.Dispose();

    public void StopNewMemberEvents()
    {
        _newMemberHandler.Dispose();
        _newMemberWelcomeService.StopAccepting();
        _newMemberWelcomeService.StopAsync().GetAwaiter().GetResult();
    }

    public Task StopEditedMessageEventsAsync() => _fortuneMessageEditHandler.StopAsync();

    public async Task<bool> StopCommandServicesAsync()
    {
        var result = await _commandHandler.StopAsync();
        return result.IsDrained;
    }

    public Task StopCommandRepliesAsync() => _commandReplySender.StopAsync();

    public void StopMessageWaiter() => _messageWaiter.Dispose();

    public void StopPaginator() => _paginatorService.Dispose();

    public void UnsubscribeDiscordLog() => _discordClient.Log -= _logHandler.LogMessages;

    public Task StopGatewayRecoveryAsync()
    {
        lock (_sideEffectStopSync)
        {
            return _gatewayRecoveryStopTask ??= _discordGatewayRecovery.DisposeAsync().AsTask();
        }
    }

    public void UnsubscribeApplicationEvents()
    {
        _discordClient.Ready -= OnDiscordReadyAsync;
        _discordClient.Disconnected -= OnDiscordDisconnectedAsync;
        AppDomain.CurrentDomain.UnhandledException -= HandleUnhandledException;
        TaskScheduler.UnobservedTaskException -= HandleUnobservedTaskException;
        _applicationStoppingRegistration.Dispose();
    }

    public Task StopPunServiceAsync()
    {
        lock (_sideEffectStopSync)
        {
            return _punStopTask ??= _dailyPunService.DisposeAsync().AsTask();
        }
    }

    public Task ReleaseInstanceLeaseAsync(CancellationToken cancellationToken)
        => _instanceLease.ReleaseAsync(cancellationToken);

    public void SkipInstanceLeaseRelease()
    {
        _instanceLease.SuppressRelease();
        BeanBotLog.InstanceLeaseReleaseSkipped(_logger);
    }

    public Task StopHealthServerAsync(CancellationToken cancellationToken)
        => _healthCheckServer.StopAsync(cancellationToken);

    public Task FlushOwnerAlertsAsync()
        => _ownerErrorNotifier.FlushAsync(TimeSpan.FromSeconds(3));

    public Task StopOwnerAlertsAsync()
    {
        _ownerErrorNotifier.StopAccepting();
        lock (_sideEffectStopSync)
        {
            return _ownerAlertStopTask ??= _ownerErrorNotifier.DisposeAsync().AsTask();
        }
    }

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _applicationStoppingRegistration.Dispose();
        _sideEffectCancellation.Dispose();
    }

    internal static async Task<bool> RunLeaseOwnedOperationAsync(
        IInstanceLeaseHealth leaseHealth,
        Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(leaseHealth);
        ArgumentNullException.ThrowIfNull(operation);
        if (!leaseHealth.IsHeld)
        {
            return false;
        }

        await operation();
        return true;
    }

    internal static bool RunLeaseOwnedOperation(
        IInstanceLeaseHealth leaseHealth,
        Func<bool> operation)
    {
        ArgumentNullException.ThrowIfNull(leaseHealth);
        ArgumentNullException.ThrowIfNull(operation);
        return leaseHealth.IsHeld && operation();
    }

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
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var loginState = _discordClient.LoginState;
            var connectionState = _discordClient.ConnectionState;
            BeanBotLog.DiscordReady(
                _logger,
                loginState,
                connectionState);
        }

        await RunTrackedOwnedDiscordReadyAsync();
    }

    private async Task RunTrackedOwnedDiscordReadyAsync()
    {
        if (!CanAdmitSideEffects())
        {
            return;
        }

        Interlocked.Increment(ref _ownedReadyOperationCount);
        try
        {
            if (!CanAdmitSideEffects())
            {
                return;
            }

            await ProcessOwnedDiscordReadyAsync();
        }
        finally
        {
            Interlocked.Decrement(ref _ownedReadyOperationCount);
        }
    }

    private async Task ProcessOwnedDiscordReadyAsync()
    {
        lock (_sideEffectStopSync)
        {
            if (!CanAdmitSideEffects())
            {
                return;
            }

            _discordGatewayRecovery.NotifyReady();
        }

        try
        {
            await _discordOutageRecoveryNotifier.NotifyIfOutageRecoveredAsync(
                DateTimeOffset.UtcNow,
                _sideEffectCancellation.Token);
        }
        catch (OperationCanceledException) when (_sideEffectCancellation.IsCancellationRequested)
        {
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

        lock (_sideEffectStopSync)
        {
            if (CanAdmitSideEffects())
            {
                _discordGatewayRecovery.StartMonitoring();
            }
        }
        return Task.CompletedTask;
    }

    private bool CanAdmitSideEffects()
        => Volatile.Read(ref _sideEffectAdmissionStopped) == 0 && _instanceLease.IsHeld;

    private void StopSideEffectAdmission()
    {
        if (Interlocked.Exchange(ref _sideEffectAdmissionStopped, 1) != 0)
        {
            return;
        }

        _ownerErrorNotifier.StopAccepting();
        _sideEffectCancellation.Cancel();

        lock (_sideEffectStopSync)
        {
            _commandHandler.Dispose();
            _reactionRoleHandler.Dispose();
            _newMemberHandler.Dispose();
            _newMemberWelcomeService.StopAccepting();
            _fortuneMessageEditHandler.Dispose();
            _messageWaiter.Dispose();
            _paginatorService.Dispose();
            _discordClient.Log -= _logHandler.LogMessages;
            _gatewayRecoveryStopTask ??= _discordGatewayRecovery.DisposeAsync().AsTask();
            _punStopTask ??= _dailyPunService.DisposeAsync().AsTask();
            _ownerAlertStopTask ??= _ownerErrorNotifier.DisposeAsync().AsTask();
        }
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
