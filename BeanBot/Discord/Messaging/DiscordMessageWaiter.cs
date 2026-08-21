using System.Collections.Concurrent;
using Discord.Commands;
using Discord.WebSocket;

namespace BeanBot.Discord.Messaging;

internal enum InteractionSessionAcquireResult
{
    Acquired,
    AlreadyActive,
    CapacityReached
}

public sealed class DiscordMessageWaiter : IDisposable
{
    internal const int MaximumPendingWaits = 64;
    internal const int MaximumInteractionSessions = 64;
    private readonly BoundedMessageWaiter<SocketMessage> _waiter = new(MaximumPendingWaits);
    private readonly BoundedInteractionSessionRegistry _sessions = new(MaximumInteractionSessions);

    public Task<SocketMessage?> WaitForNextMessageAsync(
        SocketCommandContext context,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _waiter.WaitAsync(
            context.User.Id,
            context.Channel.Id,
            timeout,
            cancellationToken);
    }

    internal InteractionSessionAcquireResult AcquireInteractionSession(
        SocketCommandContext context,
        out IDisposable? lease)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sessions.Acquire(context.User.Id, context.Channel.Id, out lease);
    }

    public bool TryPublish(SocketMessage message)
    {
        if (message == null)
        {
            return false;
        }

        return _waiter.TryPublish(
            message.Author.Id,
            message.Channel.Id,
            message.Author.IsBot,
            message);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _waiter.Dispose();
    }
}

internal sealed class BoundedInteractionSessionRegistry : IDisposable
{
    private readonly Dictionary<InteractionSessionKey, InteractionSessionLease> _activeSessions = [];
    private readonly SemaphoreSlim _availableSlots;
    private readonly object _syncRoot = new();
    private int _disposed;

    public BoundedInteractionSessionRegistry(int maximumSessions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSessions);
        _availableSlots = new SemaphoreSlim(maximumSessions, maximumSessions);
    }

    internal int ActiveCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _activeSessions.Count;
            }
        }
    }

    public InteractionSessionAcquireResult Acquire(
        ulong userId,
        ulong channelId,
        out IDisposable? lease)
    {
        lease = null;
        ThrowIfDisposed();

        lock (_syncRoot)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(BoundedInteractionSessionRegistry));
            }

            var key = new InteractionSessionKey(userId, channelId);
            if (_activeSessions.ContainsKey(key))
            {
                return InteractionSessionAcquireResult.AlreadyActive;
            }

            if (!_availableSlots.Wait(0))
            {
                return InteractionSessionAcquireResult.CapacityReached;
            }

            var sessionLease = new InteractionSessionLease(this, key);
            _activeSessions.Add(key, sessionLease);
            lease = sessionLease;
            return InteractionSessionAcquireResult.Acquired;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _activeSessions.Clear();
        }
    }

    private void Release(InteractionSessionKey key, InteractionSessionLease lease)
    {
        lock (_syncRoot)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_activeSessions.TryGetValue(key, out var activeLease) &&
                ReferenceEquals(activeLease, lease))
            {
                _activeSessions.Remove(key);
                _availableSlots.Release();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private readonly record struct InteractionSessionKey(ulong UserId, ulong ChannelId);

    private sealed class InteractionSessionLease : IDisposable
    {
        private readonly InteractionSessionKey _key;
        private BoundedInteractionSessionRegistry? _owner;

        public InteractionSessionLease(
            BoundedInteractionSessionRegistry owner,
            InteractionSessionKey key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_key, this);
        }
    }
}

internal sealed class BoundedMessageWaiter<TMessage> : IDisposable
{
    private readonly ConcurrentDictionary<MessageWaitKey, PendingWait> _pending = new();
    private readonly SemaphoreSlim _availableSlots;
    private readonly object _syncRoot = new();
    private int _disposed;

    public BoundedMessageWaiter(int maximumPendingWaits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPendingWaits);

        _availableSlots = new SemaphoreSlim(maximumPendingWaits, maximumPendingWaits);
    }

    internal int PendingCount => _pending.Count;

    public async Task<TMessage?> WaitAsync(
        ulong userId,
        ulong channelId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (!_availableSlots.Wait(0, cancellationToken))
        {
            throw new InvalidOperationException("Too many Discord message waits are already active.");
        }

        var key = new MessageWaitKey(userId, channelId);
        var pending = new PendingWait();
        lock (_syncRoot)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _availableSlots.Release();
                throw new ObjectDisposedException(nameof(BoundedMessageWaiter<TMessage>));
            }

            if (!_pending.TryAdd(key, pending))
            {
                _availableSlots.Release();
                throw new InvalidOperationException("A Discord message wait is already active for this user and channel.");
            }
        }

        try
        {
            try
            {
                return await pending.Completion.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return default;
            }
        }
        finally
        {
            RemovePendingWait(key, pending);
            _availableSlots.Release();
        }
    }

    public bool TryPublish(
        ulong userId,
        ulong channelId,
        bool isBot,
        TMessage message)
    {
        if (isBot || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        return _pending.TryGetValue(new MessageWaitKey(userId, channelId), out var pending)
            && pending.Completion.TrySetResult(message);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var pending in _pending.Values)
            {
                pending.Completion.TrySetException(new ObjectDisposedException(nameof(BoundedMessageWaiter<TMessage>)));
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private void RemovePendingWait(MessageWaitKey key, PendingWait pending)
    {
        var pair = new KeyValuePair<MessageWaitKey, PendingWait>(key, pending);
        ((ICollection<KeyValuePair<MessageWaitKey, PendingWait>>)_pending).Remove(pair);
    }

    private readonly record struct MessageWaitKey(ulong UserId, ulong ChannelId);

    private sealed class PendingWait
    {
        public TaskCompletionSource<TMessage> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
