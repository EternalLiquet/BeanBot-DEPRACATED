using BeanBot.Logging;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Interactions;

internal sealed class InteractionOperationTracker : IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly HashSet<Task> _operations = [];
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdownCancellation;
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;
    private int _disposeStarted;

    internal InteractionOperationTracker(
        TimeSpan drainTimeout,
        ILogger logger,
        CancellationToken applicationStopping = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(drainTimeout, TimeSpan.Zero);
        _drainTimeout = drainTimeout;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
    }

    internal Task TrackAsync(Func<CancellationToken, Task> beginOperation)
    {
        ArgumentNullException.ThrowIfNull(beginOperation);

        Task operation;
        lock (_syncRoot)
        {
            if (_stopping || _shutdownCancellation.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            operation = beginOperation(_shutdownCancellation.Token);
            _operations.Add(operation);
        }

        return ObserveAsync(operation);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _disposeCompletion.Task;
            return;
        }

        try
        {
            Task[] operations;
            lock (_syncRoot)
            {
                _stopping = true;
                operations = [.. _operations];
            }

            _shutdownCancellation.Cancel();
            if (operations.Length == 0)
            {
                _shutdownCancellation.Dispose();
                return;
            }

            var drainTask = Task.WhenAll(operations);
            try
            {
                await drainTask.WaitAsync(_drainTimeout);
                _shutdownCancellation.Dispose();
            }
            catch (TimeoutException)
            {
                BeanBotLog.InteractionDrainTimedOut(_logger, operations.Length, _drainTimeout);
                DisposeAfterCompletion(drainTask, _shutdownCancellation);
            }
            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
            {
                _shutdownCancellation.Dispose();
            }
            catch (Exception exception)
            {
                BeanBotLog.InteractionDrainFailed(_logger, exception);
                _shutdownCancellation.Dispose();
            }
        }
        finally
        {
            _disposeCompletion.TrySetResult();
        }
    }

    private async Task ObserveAsync(Task operation)
    {
        try
        {
            await operation;
        }
        finally
        {
            lock (_syncRoot)
            {
                _operations.Remove(operation);
            }
        }
    }

    private static void DisposeAfterCompletion(
        Task operation,
        CancellationTokenSource cancellation)
        => _ = operation.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
