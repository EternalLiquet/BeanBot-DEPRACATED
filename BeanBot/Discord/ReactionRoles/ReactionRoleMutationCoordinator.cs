using BeanBot.Logging;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.ReactionRoles;

internal readonly record struct ReactionRoleMutationKey(
    ulong GuildId,
    ulong UserId,
    ulong RoleId);

internal sealed class ReactionRoleMutationCoordinator
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<ReactionRoleMutationKey, Entry> _entries = [];
    private readonly int _capacity;
    private readonly ILogger _logger;

    internal ReactionRoleMutationCoordinator(int capacity, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        _capacity = capacity;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal int ActiveKeyCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _entries.Count;
            }
        }
    }

    internal Task? Submit(
        ReactionRoleMutationKey key,
        bool desiredState,
        Func<bool, CancellationToken, Task> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        Entry entry;
        lock (_syncRoot)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (_entries.TryGetValue(key, out var existing))
            {
                existing.DesiredState = desiredState;
                existing.Mutate = mutate;
                return null;
            }

            if (_entries.Count >= _capacity)
            {
                BeanBotLog.ReactionRoleCoordinationCapacityExceeded(_logger, _capacity);
                return null;
            }

            entry = new Entry(desiredState, mutate);
            _entries.Add(key, entry);
        }

        return RunEntryAsync(key, entry, cancellationToken);
    }

    private async Task RunEntryAsync(
        ReactionRoleMutationKey key,
        Entry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool desiredState;
                Func<bool, CancellationToken, Task> mutate;
                lock (_syncRoot)
                {
                    desiredState = entry.DesiredState;
                    mutate = entry.Mutate;
                }

                await mutate(desiredState, cancellationToken);

                lock (_syncRoot)
                {
                    if (entry.DesiredState != desiredState)
                    {
                        continue;
                    }

                    RemoveEntryIfCurrent(key, entry);
                    return;
                }
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                RemoveEntryIfCurrent(key, entry);
            }
        }
    }

    private void RemoveEntryIfCurrent(ReactionRoleMutationKey key, Entry entry)
    {
        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(key);
        }
    }

    internal static async Task RunBoundedAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(timeout);

        var operationTask = operation(operationCancellation.Token);
        try
        {
            await operationTask.WaitAsync(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateFault(operationTask);
            throw;
        }
        catch (OperationCanceledException exception) when (operationCancellation.IsCancellationRequested)
        {
            ObserveLateFault(operationTask);
            throw new TimeoutException("Discord reaction-role mutation exceeded its bounded wait.", exception);
        }
    }

    private static void ObserveLateFault(Task operationTask)
    {
        if (operationTask.IsCompleted)
        {
            _ = operationTask.Exception;
            return;
        }

        _ = operationTask.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class Entry(
        bool desiredState,
        Func<bool, CancellationToken, Task> mutate)
    {
        internal bool DesiredState { get; set; } = desiredState;
        internal Func<bool, CancellationToken, Task> Mutate { get; set; } = mutate;
    }
}
