using BeanBot.Logging;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.ReactionRoles;

internal readonly record struct ReactionRoleMutationKey(ulong GuildId, ulong UserId, ulong RoleId);

internal sealed class ReactionRoleMutationCoordinator
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<ReactionRoleMutationKey, Entry> _entries = [];
    private readonly int _capacity;
    private readonly TimeSpan _operationTimeout;
    private readonly ILogger _logger;
    private bool _stopping;

    internal ReactionRoleMutationCoordinator(int capacity, ILogger logger, TimeSpan? operationTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        _capacity = capacity;
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_operationTimeout, TimeSpan.Zero);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal int ActiveKeyCount
    {
        get { lock (_syncRoot) { return _entries.Count; } }
    }

    internal Task? Submit(
        ReactionRoleMutationKey key, ulong messageId, bool desiredState,
        Func<bool, CancellationToken, Task> mutate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        Entry entry;
        TaskCompletionSource start;
        lock (_syncRoot)
        {
            if (_stopping || cancellationToken.IsCancellationRequested) return null;
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.MessageId = messageId;
                existing.DesiredState = desiredState;
                existing.Mutate = mutate;
                existing.Version++;
                return null;
            }
            if (_entries.Count >= _capacity)
            {
                BeanBotLog.ReactionRoleCoordinationCapacityExceeded(_logger, _capacity);
                return null;
            }

            entry = new Entry(messageId, desiredState, mutate);
            start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _entries.Add(key, entry);
            entry.Worker = RunEntryAsync(key, entry, start.Task, cancellationToken);
            ObserveFailure(entry.Worker);
        }
        start.SetResult();
        // The gateway wait is finite. The worker and its key remain owned through actual REST completion.
        return entry.Worker.WaitAsync(_operationTimeout, cancellationToken);
    }

    internal Task[] SnapshotOperations()
    {
        lock (_syncRoot) { return [.. _entries.Values.Select(entry => entry.Worker)]; }
    }

    internal Task StopAsync()
    {
        lock (_syncRoot)
        {
            _stopping = true;
            return Task.WhenAll(_entries.Values.Select(entry => entry.Worker));
        }
    }

    private async Task RunEntryAsync(
        ReactionRoleMutationKey key, Entry entry, Task start, CancellationToken cancellationToken)
    {
        await start;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong messageId;
                bool desiredState;
                long version;
                Func<bool, CancellationToken, Task> mutate;
                lock (_syncRoot)
                {
                    if (_stopping) return;
                    messageId = entry.MessageId;
                    desiredState = entry.DesiredState;
                    version = entry.Version;
                    mutate = entry.Mutate;
                }
                var succeeded = false;
                try
                {
                    using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    operationCancellation.CancelAfter(_operationTimeout);
                    operationCancellation.Token.ThrowIfCancellationRequested();
                    await mutate(desiredState, operationCancellation.Token);
                    succeeded = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    BeanBotLog.ReactionRoleActionFailed(_logger, desiredState ? "add" : "remove", messageId, exception);
                }
                lock (_syncRoot)
                {
                    if (!_stopping && !cancellationToken.IsCancellationRequested
                        && entry.Version > version && (!succeeded || entry.DesiredState != desiredState)) continue;
                    // Failed attempts need a newer real event, never a blind retry.
                    RemoveEntryIfCurrent(key, entry);
                    return;
                }
            }
        }
        finally
        {
            lock (_syncRoot) { RemoveEntryIfCurrent(key, entry); }
        }
    }

    private void RemoveEntryIfCurrent(ReactionRoleMutationKey key, Entry entry)
    {
        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _entries.Remove(key);
    }

    private static void ObserveFailure(Task operation)
        => _ = operation.ContinueWith(
            completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed class Entry(ulong messageId, bool desiredState, Func<bool, CancellationToken, Task> mutate)
    {
        internal ulong MessageId { get; set; } = messageId;
        internal bool DesiredState { get; set; } = desiredState;
        internal Func<bool, CancellationToken, Task> Mutate { get; set; } = mutate;
        internal long Version { get; set; }
        internal Task Worker { get; set; } = Task.CompletedTask;
    }
}
