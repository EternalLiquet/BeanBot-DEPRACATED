namespace BeanBot.Discord.Interactions;

internal sealed class InteractionCommandRegistration
{
    private readonly object _syncRoot = new();
    private readonly Func<Task> _registerCommands;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _applicationStopping;
    private Task? _registrationTask;
    private Task? _successfulRegistrationTask;
    private bool _registered;
    private bool _successReported;
    private bool _stopping;

    public InteractionCommandRegistration(
        Func<Task> registerCommands,
        TimeSpan timeout,
        CancellationToken applicationStopping = default)
    {
        _registerCommands = registerCommands ?? throw new ArgumentNullException(nameof(registerCommands));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
        _applicationStopping = applicationStopping;
    }

    internal bool HasPendingOperations
    {
        get
        {
            lock (_syncRoot)
            {
                return _registrationTask is { IsCompleted: false };
            }
        }
    }

    internal async Task StopAsync(TimeSpan drainTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(drainTimeout, TimeSpan.Zero);
        Task? registration;
        lock (_syncRoot)
        {
            _stopping = true;
            registration = _registrationTask;
        }

        if (registration is null)
        {
            return;
        }

        try
        {
            await registration.WaitAsync(drainTimeout);
        }
        catch (TimeoutException)
        {
            // Keep the raw task owned until its completion observer releases it.
        }
        catch (Exception) when (registration.IsCompleted)
        {
            // The registration waiter reports failure; completion observes late faults.
            _ = registration.Exception;
        }
    }

    public async Task<bool> EnsureRegisteredAsync()
    {
        Task registrationTask;
        var observeCompletion = false;
        lock (_syncRoot)
        {
            if (_stopping || _applicationStopping.IsCancellationRequested)
            {
                return false;
            }

            if (_registrationTask?.IsCompleted == true)
            {
                CompleteRegistrationUnsafe(_registrationTask);
            }

            if (_registered)
            {
                return TryClaimSuccessReportUnsafe();
            }

            if (_registrationTask is null)
            {
                _registrationTask = _registerCommands();
                observeCompletion = true;
            }

            registrationTask = _registrationTask;
        }

        if (observeCompletion)
        {
            ObserveCompletion(registrationTask);
        }

        try
        {
            await registrationTask.WaitAsync(_timeout);
        }
        catch
        {
            CompleteRegistration(registrationTask);
            throw;
        }

        CompleteRegistration(registrationTask);
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_successfulRegistrationTask, registrationTask))
            {
                return false;
            }

            return TryClaimSuccessReportUnsafe();
        }
    }

    private bool TryClaimSuccessReportUnsafe()
    {
        if (!_registered || _successReported)
        {
            return false;
        }

        _successReported = true;
        return true;
    }

    private void ObserveCompletion(Task registrationTask)
        => _ = registrationTask.ContinueWith(
            CompleteRegistration,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void CompleteRegistration(Task registrationTask)
    {
        if (!registrationTask.IsCompleted)
        {
            return;
        }

        lock (_syncRoot)
        {
            CompleteRegistrationUnsafe(registrationTask);
        }
    }

    private void CompleteRegistrationUnsafe(Task registrationTask)
    {
        _ = registrationTask.Exception;
        if (!ReferenceEquals(_registrationTask, registrationTask))
        {
            return;
        }

        _registrationTask = null;
        if (registrationTask.Status == TaskStatus.RanToCompletion)
        {
            _registered = true;
            _successfulRegistrationTask = registrationTask;
        }
    }
}
