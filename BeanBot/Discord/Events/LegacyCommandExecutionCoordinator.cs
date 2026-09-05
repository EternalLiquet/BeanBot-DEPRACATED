namespace BeanBot.Discord.Events;

internal enum LegacyCommandAdmissionResult
{
    Executed,
    RejectedStopping,
    RejectedCapacity
}

internal readonly record struct LegacyCommandDrainResult(
    bool IsDrained,
    int SurvivingExecutionCount);

internal sealed class LegacyCommandExecutionCoordinator
{
    internal const int DefaultMaximumConcurrentExecutions = 16;
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly HashSet<TrackedExecution> _activeExecutions = [];
    private readonly int _maximumConcurrentExecutions;
    private readonly TimeSpan _drainTimeout;
    private bool _stopping;

    internal LegacyCommandExecutionCoordinator(
        int maximumConcurrentExecutions = DefaultMaximumConcurrentExecutions,
        TimeSpan? drainTimeout = null)
    {
        if (maximumConcurrentExecutions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentExecutions));
        }

        _maximumConcurrentExecutions = maximumConcurrentExecutions;
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        if (_drainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        }
    }

    internal int MaximumConcurrentExecutions => _maximumConcurrentExecutions;

    internal int ActiveExecutionCount
    {
        get
        {
            lock (_gate)
            {
                return _activeExecutions.Count;
            }
        }
    }

    internal bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _stopping;
            }
        }
    }

    internal async Task<LegacyCommandAdmissionResult> TryExecuteAsync(Func<Task> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);

        TrackedExecution trackedExecution;
        TaskCompletionSource<Func<Task>> startSource;
        lock (_gate)
        {
            if (_stopping)
            {
                return LegacyCommandAdmissionResult.RejectedStopping;
            }

            if (_activeExecutions.Count >= _maximumConcurrentExecutions)
            {
                return LegacyCommandAdmissionResult.RejectedCapacity;
            }

            trackedExecution = new TrackedExecution();
            startSource = new TaskCompletionSource<Func<Task>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            trackedExecution.ExecutionTask = RunTrackedExecutionAsync(
                trackedExecution,
                startSource.Task);
            _activeExecutions.Add(trackedExecution);
        }

        startSource.TrySetResult(execute);
        await trackedExecution.ExecutionTask.ConfigureAwait(false);
        return LegacyCommandAdmissionResult.Executed;
    }

    internal void StopAdmission()
    {
        lock (_gate)
        {
            _stopping = true;
        }
    }

    internal async Task<LegacyCommandDrainResult> DrainAsync(Action<Exception> lateFailureObserver)
    {
        ArgumentNullException.ThrowIfNull(lateFailureObserver);

        TrackedExecution[] snapshot;
        lock (_gate)
        {
            _stopping = true;
            snapshot = [.. _activeExecutions];
        }

        if (snapshot.Length == 0)
        {
            return new LegacyCommandDrainResult(true, 0);
        }

        var completionTasks = snapshot
            .Select(static execution => ObserveCompletionAsync(execution.ExecutionTask))
            .ToArray();

        try
        {
            await Task.WhenAll(completionTasks)
                .WaitAsync(_drainTimeout)
                .ConfigureAwait(false);
            return new LegacyCommandDrainResult(true, 0);
        }
        catch (TimeoutException)
        {
            TrackedExecution[] survivors;
            lock (_gate)
            {
                survivors = [.. _activeExecutions];
            }

            foreach (var survivor in survivors)
            {
                _ = survivor.ExecutionTask.ContinueWith(
                    static (completedTask, state) =>
                    {
                        var observer = (Action<Exception>)state!;
                        observer(completedTask.Exception!.GetBaseException());
                    },
                    lateFailureObserver,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return new LegacyCommandDrainResult(false, survivors.Length);
        }
    }

    private async Task RunTrackedExecutionAsync(
        TrackedExecution trackedExecution,
        Task<Func<Task>> startTask)
    {
        try
        {
            var execute = await startTask.ConfigureAwait(false);
            await execute().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _activeExecutions.Remove(trackedExecution);
            }
        }
    }

    private static async Task ObserveCompletionAsync(Task executionTask)
    {
        try
        {
            await executionTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The command event caller receives the original fault. Draining only needs
            // to observe completion so command failures do not become shutdown failures.
        }
    }

    private sealed class TrackedExecution
    {
        public Task ExecutionTask { get; set; } = Task.CompletedTask;
    }
}
