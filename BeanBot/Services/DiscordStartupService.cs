using System.Net;
using BeanBot.Util;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Services;

internal sealed class DiscordStartupOptions
{
    public static DiscordStartupOptions Default { get; } = new(
        3,
        TimeSpan.FromSeconds(30),
        attempt => attempt == 1
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromSeconds(15));

    public DiscordStartupOptions(
        int maximumAttempts,
        TimeSpan lifecycleOperationTimeout,
        Func<int, TimeSpan> retryDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifecycleOperationTimeout, TimeSpan.Zero);

        MaximumAttempts = maximumAttempts;
        LifecycleOperationTimeout = lifecycleOperationTimeout;
        RetryDelay = retryDelay ?? throw new ArgumentNullException(nameof(retryDelay));
    }

    public int MaximumAttempts { get; }
    public TimeSpan LifecycleOperationTimeout { get; }
    public Func<int, TimeSpan> RetryDelay { get; }
}

internal interface IDiscordStartupLifecycle
{
    Task LoginAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task SetPresenceAsync(CancellationToken cancellationToken);
}

internal interface IDiscordStartupDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class DiscordStartupDelay : IDiscordStartupDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}

internal sealed class DiscordStartupLifecycle : IDiscordStartupLifecycle
{
    private const string GameStatus = "My purpose is to bully Hatate and succ the world dry";
    private readonly object _syncRoot = new();
    private readonly Func<Task> _login;
    private readonly Func<Task> _start;
    private readonly Func<Task> _setPresence;
    private readonly TimeSpan _operationTimeout;
    private readonly Action<Exception, string> _lateFailureObserver;
    private readonly ILogger<DiscordStartupLifecycle> _logger;
    private Task? _unfinishedOperation;

    public DiscordStartupLifecycle(
        DiscordSocketClient client,
        string botToken,
        TimeSpan operationTimeout,
        ILogger<DiscordStartupLifecycle> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(botToken);
        _login = () => client.LoginAsync(TokenType.Bot, botToken);
        _start = client.StartAsync;
        _setPresence = () => client.SetGameAsync(GameStatus, null, ActivityType.Playing);
        _operationTimeout = operationTimeout;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lateFailureObserver = LogLateFailure;
    }

    internal DiscordStartupLifecycle(
        Func<Task> login,
        Func<Task> start,
        Func<Task> setPresence,
        TimeSpan operationTimeout,
        ILogger<DiscordStartupLifecycle> logger,
        Action<Exception, string>? lateFailureObserver = null)
    {
        _login = login ?? throw new ArgumentNullException(nameof(login));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _setPresence = setPresence ?? throw new ArgumentNullException(nameof(setPresence));
        _operationTimeout = operationTimeout;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lateFailureObserver = lateFailureObserver ?? LogLateFailure;
    }

    internal bool HasUnfinishedOperation
    {
        get
        {
            lock (_syncRoot)
            {
                return _unfinishedOperation?.IsCompleted == false;
            }
        }
    }

    public Task LoginAsync(CancellationToken cancellationToken)
        => RunBoundedAsync(
            _login,
            "login",
            cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken)
        => RunBoundedAsync(
            _start,
            "start",
            cancellationToken);

    public Task SetPresenceAsync(CancellationToken cancellationToken)
        => RunBoundedAsync(
            _setPresence,
            "presence",
            cancellationToken);

    private async Task RunBoundedAsync(
        Func<Task> beginOperation,
        string operationName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = beginOperation();
        try
        {
            // Discord.Net does not accept cancellation tokens for these operations,
            // so BeanBot bounds its own wait and records an abandoned operation. The
            // caller can then avoid racing teardown against the same client.
            await operation.WaitAsync(_operationTimeout, cancellationToken);
        }
        catch
        {
            if (!operation.IsCompleted)
            {
                TrackUnfinishedOperation(operation, operationName);
            }

            throw;
        }
    }

    private void TrackUnfinishedOperation(Task operation, string operationName)
    {
        lock (_syncRoot)
        {
            _unfinishedOperation = operation;
        }

        _ = operation.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted)
                {
                    _lateFailureObserver(completedTask.Exception!, operationName);
                }

                lock (_syncRoot)
                {
                    if (ReferenceEquals(_unfinishedOperation, completedTask))
                    {
                        _unfinishedOperation = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void LogLateFailure(Exception exception, string operationName)
        => BeanBotLog.DiscordStartupLateFailure(_logger, operationName, exception);
}

internal sealed class DiscordStartupService
{
    private readonly IDiscordStartupLifecycle _lifecycle;
    private readonly DiscordStartupOptions _options;
    private readonly IDiscordStartupDelay _delay;
    private readonly ILogger<DiscordStartupService> _logger;

    public DiscordStartupService(
        IDiscordStartupLifecycle lifecycle,
        ILogger<DiscordStartupService> logger,
        DiscordStartupOptions? options = null,
        IDiscordStartupDelay? delay = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? DiscordStartupOptions.Default;
        _delay = delay ?? new DiscordStartupDelay();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoginWithRetryAsync(cancellationToken);
        await _lifecycle.StartAsync(cancellationToken);

        try
        {
            await _lifecycle.SetPresenceAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            // A timed-out presence task may still own the Discord client. Propagate
            // so startup teardown can avoid racing it with runtime lifecycle work.
            throw;
        }
        catch (Exception exception)
        {
            // A completed presence failure is cosmetic and must not fail an
            // otherwise valid gateway lifecycle.
            BeanBotLog.DiscordPresenceFailed(_logger, exception);
        }
    }

    private async Task LoginWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _options.MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeanBotLog.DiscordLoginAttempting(
                _logger,
                attempt,
                _options.MaximumAttempts);

            try
            {
                await _lifecycle.LoginAsync(cancellationToken);
                BeanBotLog.DiscordLoginSucceeded(
                    _logger,
                    attempt,
                    _options.MaximumAttempts);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Discord.Net.HttpException exception) when (exception.HttpCode == HttpStatusCode.Unauthorized)
            {
                BeanBotLog.DiscordTokenRejected(
                    _logger,
                    attempt,
                    _options.MaximumAttempts,
                    exception);
                throw;
            }
            catch (Discord.Net.HttpException exception)
            {
                if (attempt == _options.MaximumAttempts)
                {
                    BeanBotLog.DiscordLoginExhausted(
                        _logger,
                        attempt,
                        _options.MaximumAttempts,
                        exception);
                    throw;
                }

                var retryDelay = _options.RetryDelay(attempt);
                BeanBotLog.DiscordLoginRetrying(
                    _logger,
                    attempt,
                    _options.MaximumAttempts,
                    retryDelay,
                    exception);
                await _delay.DelayAsync(retryDelay, cancellationToken);
            }
            catch (Exception exception)
            {
                BeanBotLog.DiscordLoginFailed(
                    _logger,
                    attempt,
                    _options.MaximumAttempts,
                    exception);
                throw;
            }
        }
    }
}
