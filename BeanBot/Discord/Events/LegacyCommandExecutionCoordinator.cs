namespace BeanBot.Discord.Events;

internal enum LegacyCommandAdmissionResult
{
    Admitted,
    RejectedStopping,
    RejectedCapacity
}

internal readonly record struct LegacyCommandDrainResult(bool IsDrained, int SurvivingExecutionCount);

internal sealed class LegacyCommandExecutionCoordinator
{
    internal const int DefaultMaximumConcurrentExecutions = 16;
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly HashSet<Task> _activeExecutions = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _maximumConcurrentExecutions;
    private readonly TimeSpan _drainTimeout;
    private bool _stopping;

    internal LegacyCommandExecutionCoordinator(
        int maximumConcurrentExecutions = DefaultMaximumConcurrentExecutions,
        TimeSpan? drainTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrentExecutions);
        _maximumConcurrentExecutions = maximumConcurrentExecutions;
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_drainTimeout, TimeSpan.Zero);
    }

    internal int MaximumConcurrentExecutions => _maximumConcurrentExecutions;
    internal Task WhenDrained => _drained.Task;
    internal int ActiveExecutionCount
    {
        get
        {
            lock (_gate) { return _activeExecutions.Count; }
        }
    }

    internal bool IsStopping
    {
        get
        {
            lock (_gate) { return _stopping; }
        }
    }

    internal LegacyCommandAdmissionResult TryStart(
        Func<Task> execute,
        Action<Exception> reportFailure,
        out Task completion)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(reportFailure);
        TaskCompletionSource start;
        lock (_gate)
        {
            completion = Task.CompletedTask;
            if (_stopping)
            {
                return LegacyCommandAdmissionResult.RejectedStopping;
            }
            if (_activeExecutions.Count >= _maximumConcurrentExecutions)
            {
                return LegacyCommandAdmissionResult.RejectedCapacity;
            }

            start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = RunAsync(start.Task, execute, reportFailure);
            _activeExecutions.Add(completion);
            _ = completion.ContinueWith(CompleteExecution, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        start.SetResult();
        return LegacyCommandAdmissionResult.Admitted;
    }

    internal void StopAdmission()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_activeExecutions.Count == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    internal async Task<LegacyCommandDrainResult> DrainAsync()
    {
        StopAdmission();
        try
        {
            await WhenDrained.WaitAsync(_drainTimeout).ConfigureAwait(false);
            return new LegacyCommandDrainResult(true, 0);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                return new LegacyCommandDrainResult(_activeExecutions.Count == 0, _activeExecutions.Count);
            }
        }
    }

    private static async Task RunAsync(Task start, Func<Task> execute, Action<Exception> reportFailure)
    {
        await start.ConfigureAwait(false);
        try
        {
            await execute().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                reportFailure(exception);
            }
            catch (Exception)
            {
                // Reporting must not replace the original failure or create an unowned fault.
            }
            throw;
        }
    }

    private void CompleteExecution(Task completion)
    {
        _ = completion.Exception;
        lock (_gate)
        {
            _activeExecutions.Remove(completion);
            if (_stopping && _activeExecutions.Count == 0)
            {
                _drained.TrySetResult();
            }
        }
    }
}
