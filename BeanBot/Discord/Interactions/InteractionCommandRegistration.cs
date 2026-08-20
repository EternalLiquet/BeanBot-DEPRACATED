namespace BeanBot.Discord.Interactions;

internal sealed class InteractionCommandRegistration
{
    private readonly object _syncRoot = new();
    private readonly Func<Task> _registerCommands;
    private readonly TimeSpan _timeout;
    private Task? _registrationTask;
    private bool _registered;

    public InteractionCommandRegistration(Func<Task> registerCommands, TimeSpan timeout)
    {
        _registerCommands = registerCommands ?? throw new ArgumentNullException(nameof(registerCommands));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
    }

    public async Task<bool> EnsureRegisteredAsync()
    {
        Task registrationTask;
        lock (_syncRoot)
        {
            if (_registered)
            {
                return false;
            }

            _registrationTask ??= _registerCommands();
            registrationTask = _registrationTask;
        }

        try
        {
            await registrationTask.WaitAsync(_timeout);
        }
        catch (TimeoutException)
        {
            ObserveLateFault(registrationTask);
            throw;
        }
        catch
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_registrationTask, registrationTask))
                {
                    _registrationTask = null;
                }
            }

            throw;
        }

        lock (_syncRoot)
        {
            if (_registered)
            {
                return false;
            }

            _registered = true;
            if (ReferenceEquals(_registrationTask, registrationTask))
            {
                _registrationTask = null;
            }

            return true;
        }
    }

    private static void ObserveLateFault(Task registrationTask)
    {
        if (registrationTask.IsCompleted)
        {
            _ = registrationTask.Exception;
            return;
        }

        _ = registrationTask.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
