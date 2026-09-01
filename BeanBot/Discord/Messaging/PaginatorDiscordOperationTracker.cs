using BeanBot.Logging;
using Discord;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Messaging;

internal sealed class PaginatorDiscordOperationTracker
{
    private readonly object _syncRoot = new();
    private readonly int _maximumOperations;
    private readonly TimeSpan _operationTimeout;
    private readonly CancellationToken _shutdownCancellation;
    private readonly ILogger<DiscordPaginatorService> _logger;
    private readonly Action<Exception, string>? _lateFailureObserver;
    private readonly TaskCompletionSource _stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _ownedOperationCount;
    private bool _stopping;

    public PaginatorDiscordOperationTracker(
        int maximumOperations,
        TimeSpan operationTimeout,
        CancellationToken shutdownCancellation,
        ILogger<DiscordPaginatorService> logger)
        : this(
            maximumOperations,
            operationTimeout,
            logger,
            lateFailureObserver: null,
            shutdownCancellation)
    {
    }

    public PaginatorDiscordOperationTracker(
        int maximumOperations,
        TimeSpan operationTimeout,
        ILogger<DiscordPaginatorService> logger,
        Action<Exception, string>? lateFailureObserver,
        CancellationToken shutdownCancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOperations);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationTimeout, TimeSpan.Zero);

        _maximumOperations = maximumOperations;
        _operationTimeout = operationTimeout;
        _shutdownCancellation = shutdownCancellation;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lateFailureObserver = lateFailureObserver;
    }

    internal int OwnedOperationCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _ownedOperationCount;
            }
        }
    }

    public Task RunAsync(
        string operationName,
        Func<RequestOptions, Task> beginOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beginOperation);

        return RunAsync<object?>(
            operationName,
            async options =>
            {
                await beginOperation(options);
                return null;
            },
            cancellationToken);
    }

    public async Task<TResult> RunAsync<TResult>(
        string operationName,
        Func<RequestOptions, Task<TResult>> beginOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(beginOperation);

        AcquireOperation(operationName, cancellationToken);
        Task<TResult>? operation = null;
        try
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCancellation,
                cancellationToken);
            operationCancellation.CancelAfter(_operationTimeout);
            operationCancellation.Token.ThrowIfCancellationRequested();

            operation = beginOperation(
                new RequestOptions
                {
                    CancelToken = operationCancellation.Token
                }) ?? throw new InvalidOperationException(
                    $"Discord paginator operation '{operationName}' returned no task.");
            _ = ObserveOperationCompletionAsync(operation);

            try
            {
                return await operation.WaitAsync(operationCancellation.Token);
            }
            catch (OperationCanceledException)
                when (_shutdownCancellation.IsCancellationRequested
                    || cancellationToken.IsCancellationRequested)
            {
                if (!operation.IsCompleted)
                {
                    ObserveLateFailure(operation, operationName);
                }

                throw;
            }
            catch (OperationCanceledException exception)
                when (operationCancellation.IsCancellationRequested)
            {
                if (!operation.IsCompleted)
                {
                    ObserveLateFailure(operation, operationName);
                }

                BeanBotLog.PaginatorDiscordOperationTimedOut(
                    _logger,
                    operationName,
                    _operationTimeout);
                throw new TimeoutException(
                    $"Discord paginator operation '{operationName}' exceeded {_operationTimeout}.",
                    exception);
            }
        }
        catch
        {
            if (operation is null)
            {
                ReleaseOperation();
            }

            throw;
        }
    }

    public Task StopAsync()
    {
        lock (_syncRoot)
        {
            _stopping = true;
            if (_ownedOperationCount == 0)
            {
                _stopped.TrySetResult();
            }

            return _stopped.Task;
        }
    }

    private void AcquireOperation(
        string operationName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _shutdownCancellation.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            if (_stopping || _shutdownCancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(_shutdownCancellation);
            }

            if (_ownedOperationCount >= _maximumOperations)
            {
                BeanBotLog.PaginatorDiscordOperationCapacityExhausted(
                    _logger,
                    operationName,
                    _maximumOperations);
                throw new InvalidOperationException(
                    $"Discord paginator operation capacity of {_maximumOperations} is exhausted.");
            }

            _ownedOperationCount++;
        }
    }

    private async Task ObserveOperationCompletionAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch
        {
            // The caller observes prompt failures. Timed-out/canceled callers attach a
            // dedicated late-failure observer before relinquishing their bounded wait.
        }
        finally
        {
            ReleaseOperation();
        }
    }

    private void ObserveLateFailure(Task operation, string operationName)
    {
        _ = operation.ContinueWith(
            completedTask =>
            {
                var exception = completedTask.Exception!.GetBaseException();
                if (_lateFailureObserver is not null)
                {
                    _lateFailureObserver(exception, operationName);
                }
                else
                {
                    BeanBotLog.PaginatorDiscordOperationLateFailure(
                        _logger,
                        operationName,
                        exception);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseOperation()
    {
        lock (_syncRoot)
        {
            _ownedOperationCount--;
            if (_stopping && _ownedOperationCount == 0)
            {
                _stopped.TrySetResult();
            }
        }
    }
}
