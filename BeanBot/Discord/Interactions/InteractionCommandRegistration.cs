namespace BeanBot.Discord.Interactions;

internal sealed class InteractionCommandRegistration
{
    private readonly object _syncRoot = new();
    private readonly Func<Task> _registerCommands;
    private readonly TimeSpan _timeout;
    private Task? _registrationTask;
    private Task? _successfulRegistrationTask;
    private bool _registered;
    private bool _successReported;

    public InteractionCommandRegistration(Func<Task> registerCommands, TimeSpan timeout)
    {
        _registerCommands = registerCommands ?? throw new ArgumentNullException(nameof(registerCommands));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
    }

    public async Task<bool> EnsureRegisteredAsync()
    {
        Task registrationTask;
        var observeCompletion = false;
        lock (_syncRoot)
        {
            if (_registrationTask?.IsCompleted == true)
            {
                CompleteRegistrationUnsafe(_registrationTask);
            }

            if (_registered)
            {
                return false;
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
            if (!ReferenceEquals(_successfulRegistrationTask, registrationTask)
                || _successReported)
            {
                return false;
            }

            _successReported = true;
            return true;
        }
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
