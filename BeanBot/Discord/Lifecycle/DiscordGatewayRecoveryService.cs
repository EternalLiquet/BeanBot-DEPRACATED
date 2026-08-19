using System.Diagnostics;
using BeanBot.Health;
using BeanBot.Logging;
using BeanBot.Persistence.Outages;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Lifecycle;

internal sealed class DiscordGatewayRecoveryOptions
{
    public static DiscordGatewayRecoveryOptions Default { get; } = new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2));

    public DiscordGatewayRecoveryOptions(
        TimeSpan builtInRecoveryGracePeriod,
        TimeSpan lifecycleOperationTimeout,
        TimeSpan readyTimeout)
    {
        BuiltInRecoveryGracePeriod = builtInRecoveryGracePeriod;
        LifecycleOperationTimeout = lifecycleOperationTimeout;
        ReadyTimeout = readyTimeout;
    }

    public TimeSpan BuiltInRecoveryGracePeriod { get; }
    public TimeSpan LifecycleOperationTimeout { get; }
    public TimeSpan ReadyTimeout { get; }
}

internal interface IDiscordGatewayLifecycle
{
    Task ReconnectAsync(CancellationToken cancellationToken);
}

internal interface IRecoveryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class TaskRecoveryDelay : IRecoveryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}

internal sealed class DiscordGatewayLifecycle : IDiscordGatewayLifecycle
{
    private readonly DiscordSocketClient _client;
    private readonly string _botToken;
    private readonly TimeSpan _operationTimeout;
    private readonly ILogger<DiscordGatewayLifecycle> _logger;

    public DiscordGatewayLifecycle(
        DiscordSocketClient client,
        string botToken,
        TimeSpan operationTimeout,
        ILogger<DiscordGatewayLifecycle> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _botToken = botToken ?? throw new ArgumentNullException(nameof(botToken));
        _operationTimeout = operationTimeout;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RunBoundedAsync(_client.StopAsync(), "stop");
        cancellationToken.ThrowIfCancellationRequested();
        await RunBoundedAsync(_client.LogoutAsync(), "logout");
        cancellationToken.ThrowIfCancellationRequested();
        await RunBoundedAsync(
            _client.LoginAsync(TokenType.Bot, _botToken),
            "login");
        cancellationToken.ThrowIfCancellationRequested();
        await RunBoundedAsync(_client.StartAsync(), "start");
    }

    private async Task RunBoundedAsync(
        Task operation,
        string operationName)
    {
        try
        {
            // Once a Discord lifecycle operation starts, let it finish (or time out)
            // instead of abandoning it midway and racing normal shutdown cleanup.
            await operation.WaitAsync(_operationTimeout);
        }
        catch
        {
            if (!operation.IsCompleted)
            {
                ObserveLateFailure(operation, operationName);
            }

            throw;
        }
    }

    private void ObserveLateFailure(Task operation, string operationName)
    {
        _ = operation.ContinueWith(
            completedTask => BeanBotLog.DiscordRecoveryLateFailure(
                _logger,
                operationName,
                completedTask.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed class DiscordGatewayRecoveryService : IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly Func<DiscordHealthSnapshot> _createHealthSnapshot;
    private readonly IDiscordGatewayLifecycle _lifecycle;
    private readonly IDiscordOutageStore _outageStore;
    private readonly DiscordGatewayRecoveryOptions _options;
    private readonly IRecoveryDelay _delay;
    private readonly Action<int> _exitProcess;
    private readonly ILogger<DiscordGatewayRecoveryService> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource<bool>? _readySignal;
    private Task? _monitorTask;
    private bool _disposed;

    public DiscordGatewayRecoveryService(
        Func<DiscordHealthSnapshot> createHealthSnapshot,
        IDiscordGatewayLifecycle lifecycle,
        IDiscordOutageStore outageStore,
        ILogger<DiscordGatewayRecoveryService> logger,
        DiscordGatewayRecoveryOptions? options = null,
        IRecoveryDelay? delay = null,
        Action<int>? exitProcess = null)
    {
        _createHealthSnapshot = createHealthSnapshot ?? throw new ArgumentNullException(nameof(createHealthSnapshot));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _outageStore = outageStore ?? throw new ArgumentNullException(nameof(outageStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? DiscordGatewayRecoveryOptions.Default;
        _delay = delay ?? new TaskRecoveryDelay();
        _exitProcess = exitProcess ?? Environment.Exit;
    }

    public bool StartMonitoring()
    {
        lock (_syncRoot)
        {
            if (_disposed || _shutdown.IsCancellationRequested || (_monitorTask?.IsCompleted == false))
            {
                return false;
            }

            var snapshot = _createHealthSnapshot();
            if (snapshot.IsHealthy)
            {
                return false;
            }

            _readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _monitorTask = MonitorAsync(snapshot);
            return true;
        }
    }

    public void NotifyReady()
    {
        lock (_syncRoot)
        {
            _readySignal?.TrySetResult(true);
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (_syncRoot)
        {
            return _monitorTask ?? Task.CompletedTask;
        }
    }

    private async Task MonitorAsync(DiscordHealthSnapshot initialSnapshot)
    {
        var unhealthySince = initialSnapshot.UnhealthySinceAtUtc ?? DateTimeOffset.UtcNow;
        BeanBotLog.DiscordRecoveryGraceStarted(
            _logger,
            initialSnapshot.LoginState,
            initialSnapshot.ConnectionState,
            _options.BuiltInRecoveryGracePeriod,
            initialSnapshot.MostRecentDisconnectReason);

        try
        {
            if (await WaitForReadyAsync(
                _options.BuiltInRecoveryGracePeriod,
                _shutdown.Token))
            {
                LogNaturalRecovery(unhealthySince);
                return;
            }

            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            var snapshot = _createHealthSnapshot();
            if (snapshot.IsHealthy)
            {
                LogNaturalRecovery(unhealthySince);
                return;
            }

            BeanBotLog.DiscordManualRecoveryStarting(
                _logger,
                DateTimeOffset.UtcNow - unhealthySince,
                snapshot.LoginState,
                snapshot.ConnectionState,
                snapshot.MostRecentDisconnectReason);

            await PersistManualRecoveryAttemptAsync(unhealthySince, snapshot);

            try
            {
                await _lifecycle.ReconnectAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                snapshot = _createHealthSnapshot();
                BeanBotLog.DiscordManualRecoveryFailed(
                    _logger,
                    snapshot.LoginState,
                    snapshot.ConnectionState,
                    snapshot.MostRecentDisconnectReason,
                    exception);
            }

            if (await WaitForReadyAsync(
                _options.ReadyTimeout,
                _shutdown.Token))
            {
                snapshot = _createHealthSnapshot();
                BeanBotLog.DiscordManualRecoverySucceeded(
                    _logger,
                    DateTimeOffset.UtcNow - unhealthySince,
                    snapshot.LoginState,
                    snapshot.ConnectionState);
                return;
            }

            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            snapshot = _createHealthSnapshot();
            if (snapshot.IsHealthy)
            {
                BeanBotLog.DiscordManualRecoverySucceeded(
                    _logger,
                    DateTimeOffset.UtcNow - unhealthySince,
                    snapshot.LoginState,
                    snapshot.ConnectionState);
                return;
            }

            BeanBotLog.DiscordManualRecoveryNotReady(
                _logger,
                DateTimeOffset.UtcNow - unhealthySince,
                snapshot.LoginState,
                snapshot.ConnectionState,
                snapshot.MostRecentDisconnectReason);

            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            BeanBotLog.DiscordRecoveryExhausted(_logger);
            await PersistRestartRequestAsync(unhealthySince, snapshot);

            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            _exitProcess(1);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            BeanBotLog.DiscordRecoveryMonitorFailed(_logger, exception);
            if (!_shutdown.IsCancellationRequested)
            {
                BeanBotLog.DiscordRecoveryMonitorExiting(_logger);
                var snapshot = _createHealthSnapshot();
                await PersistRestartRequestAsync(
                    snapshot.UnhealthySinceAtUtc ?? unhealthySince,
                    snapshot);

                if (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                _exitProcess(1);
            }
        }
    }

    private async Task PersistManualRecoveryAttemptAsync(
        DateTimeOffset unhealthySince,
        DiscordHealthSnapshot snapshot)
    {
        try
        {
            await _outageStore.MarkManualRecoveryAttemptedAsync(
                unhealthySince,
                snapshot.MostRecentDisconnectReason,
                _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // Recovery must still proceed if the durable incident record cannot be written.
            BeanBotLog.DiscordOutagePersistBeforeRecoveryFailed(_logger, unhealthySince, exception);
        }
    }

    private async Task PersistRestartRequestAsync(
        DateTimeOffset unhealthySince,
        DiscordHealthSnapshot snapshot)
    {
        try
        {
            await _outageStore.MarkProcessRestartRequestedAsync(
                unhealthySince,
                snapshot.MostRecentDisconnectReason,
                _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // The process must still exit so Docker can restore service even if persistence is unavailable.
            BeanBotLog.DiscordOutageRestartPersistFailed(_logger, exception);
        }
    }

    private async Task<bool> WaitForReadyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (_createHealthSnapshot().IsHealthy)
            {
                return true;
            }

            TaskCompletionSource<bool> readySignal;
            lock (_syncRoot)
            {
                readySignal = _readySignal
                    ?? throw new InvalidOperationException("Discord gateway recovery has no Ready signal.");
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = _delay.DelayAsync(remaining, delayCancellation.Token);
            var completedTask = await Task.WhenAny(readySignal.Task, timeoutTask);
            delayCancellation.Cancel();

            try
            {
                await completedTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (completedTask == timeoutTask)
            {
                return _createHealthSnapshot().IsHealthy;
            }

            if (_createHealthSnapshot().IsHealthy)
            {
                return true;
            }

            lock (_syncRoot)
            {
                if (ReferenceEquals(_readySignal, readySignal))
                {
                    _readySignal = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
    }

    private void LogNaturalRecovery(DateTimeOffset unhealthySince)
    {
        var snapshot = _createHealthSnapshot();
        BeanBotLog.DiscordNaturalRecovery(
            _logger,
            DateTimeOffset.UtcNow - unhealthySince,
            snapshot.LoginState,
            snapshot.ConnectionState);
    }

    public async ValueTask DisposeAsync()
    {
        Task? monitorTask;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            monitorTask = _monitorTask;
        }

        if (monitorTask is not null)
        {
            await monitorTask;
        }

        _shutdown.Dispose();
    }
}
