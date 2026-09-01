using System.Collections.Concurrent;
using BeanBot.Logging;
using Discord;
using Discord.Commands;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Messaging;

public sealed class DiscordPaginatorService : IDisposable
{
    internal const int MaximumActivePaginators = 64;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DiscordOperationTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly Emoji FirstPage = new("⏮");
    private static readonly Emoji PreviousPage = new("◀");
    private static readonly Emoji NextPage = new("▶");
    private static readonly Emoji LastPage = new("⏭");
    private static readonly Emoji Stop = new("⏹");
    private static readonly IReadOnlyCollection<IEmote> Controls = new IEmote[]
    {
        FirstPage,
        PreviousPage,
        NextPage,
        LastPage,
        Stop
    };

    private readonly DiscordSocketClient _discordClient;
    private readonly ConcurrentDictionary<ulong, PaginationSession> _sessions = new();
    private readonly SemaphoreSlim _availableSlots = new(MaximumActivePaginators, MaximumActivePaginators);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly object _syncRoot = new();
    private readonly ILogger<DiscordPaginatorService> _logger;
    private readonly PaginatorDiscordOperationTracker _discordOperations;
    private int _disposed;

    public DiscordPaginatorService(
        DiscordSocketClient discordClient,
        ILogger<DiscordPaginatorService> logger)
        : this(discordClient, logger, DiscordOperationTimeout)
    {
    }

    internal DiscordPaginatorService(
        DiscordSocketClient discordClient,
        ILogger<DiscordPaginatorService> logger,
        TimeSpan discordOperationTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            discordOperationTimeout,
            TimeSpan.Zero);

        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownToken = _shutdown.Token;
        _discordOperations = new PaginatorDiscordOperationTracker(
            MaximumActivePaginators,
            discordOperationTimeout,
            _logger,
            lateFailureObserver: null,
            _shutdownToken);
        _discordClient.ReactionAdded += HandleReactionAsync;
    }

    public async Task<IUserMessage> SendAsync(
        SocketCommandContext context,
        IReadOnlyCollection<string> pages,
        TimeSpan? timeout = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);

        var pageList = pages?.Where(page => page != null).ToList()
            ?? throw new ArgumentNullException(nameof(pages));
        if (pageList.Count == 0)
        {
            throw new ArgumentException("At least one pagination page is required.", nameof(pages));
        }

        var paginationTimeout = timeout ?? DefaultTimeout;
        if (paginationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (!_availableSlots.Wait(0))
            {
                throw new InvalidOperationException("Too many Discord paginators are already active.");
            }
        }

        RestUserMessage? message = null;
        PaginationSession? session = null;
        var sessionRegistered = false;
        var sessionAccessHeld = false;
        try
        {
            var cursor = new PaginationCursor(pageList.Count);
            message = await _discordOperations.RunAsync(
                "send message",
                options => context.Channel.SendMessageAsync(
                    embed: BuildEmbed(pageList, cursor),
                    options: options));
            session = new PaginationSession(message, context.User.Id, pageList, cursor, _shutdownToken);
            await session.Access.WaitAsync();
            sessionAccessHeld = true;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (!_sessions.TryAdd(message.Id, session))
                {
                    throw new InvalidOperationException("The Discord paginator message is already registered.");
                }
                sessionRegistered = true;
                session.ExpirationTask = ExpireAsync(message.Id, session, paginationTimeout);
            }

            foreach (var control in Controls)
            {
                await _discordOperations.RunAsync(
                    "add control reaction",
                    options => message.AddReactionAsync(control, options));
            }

            ObjectDisposedException.ThrowIf(
                !_sessions.TryGetValue(message.Id, out var currentSession)
                    || !ReferenceEquals(currentSession, session)
                    || session.CompletionStarted,
                this);

            return message;
        }
        catch
        {
            if (!sessionRegistered)
            {
                ReleaseAvailableSlot();
            }
            else if (message is not null && session is not null)
            {
                if (sessionAccessHeld)
                {
                    session.Access.Release();
                    sessionAccessHeld = false;
                }
                await StopSessionAsync(message.Id, session, deleteMessage: false);
            }

            throw;
        }
        finally
        {
            if (sessionAccessHeld)
            {
                session?.Access.Release();
            }
        }
    }

    private async Task HandleReactionAsync(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel,
        SocketReaction reaction)
    {
        if (Volatile.Read(ref _disposed) != 0
            || reaction.UserId == _discordClient.CurrentUser?.Id
            || !_sessions.TryGetValue(message.Id, out var session)
            || reaction.UserId != session.UserId)
        {
            return;
        }

        var action = GetAction(reaction.Emote);
        if (action == PaginationAction.None)
        {
            return;
        }

        var terminateAfterAmbiguousEdit = false;
        try
        {
            var stopRequested = false;
            await session.Access.WaitAsync(_shutdownToken);
            try
            {
                if (!_sessions.TryGetValue(message.Id, out var currentSession)
                    || !ReferenceEquals(currentSession, session)
                    || session.CompletionStarted)
                {
                    return;
                }

                if (action == PaginationAction.Stop)
                {
                    stopRequested = true;
                }
                else
                {
                    if (session.Cursor.Move(action))
                    {
                        try
                        {
                            await _discordOperations.RunAsync(
                                "modify page",
                                options => session.Message.ModifyAsync(
                                    properties => properties.Embed = BuildEmbed(session.Pages, session.Cursor),
                                    options));
                        }
                        catch (TimeoutException)
                        {
                            terminateAfterAmbiguousEdit = true;
                            throw;
                        }
                    }

                    await TryRemoveUserReactionAsync(
                        session.Message,
                        reaction.Emote,
                        reaction.UserId,
                        _shutdownToken);
                }
            }
            finally
            {
                session.Access.Release();
            }

            if (stopRequested)
            {
                await StopSessionAsync(message.Id, session, deleteMessage: true);
            }
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (terminateAfterAmbiguousEdit)
            {
                try
                {
                    await StopSessionAsync(message.Id, session, deleteMessage: false);
                }
                catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
                {
                }
                catch (Exception completionException)
                {
                    BeanBotLog.PaginatorReactionFailed(
                        _logger,
                        message.Id,
                        completionException);
                }
            }

            BeanBotLog.PaginatorReactionFailed(_logger, message.Id, exception);
        }
    }

    private async Task ExpireAsync(
        ulong messageId,
        PaginationSession session,
        TimeSpan timeout)
    {
        var ownsCompletion = false;
        var ownsAccess = false;
        var expirationCancellation = session.ExpirationCancellation;
        try
        {
            await Task.Delay(timeout, expirationCancellation);
            if (!session.TryBeginCompletion())
            {
                return;
            }

            ownsCompletion = true;
            await session.Access.WaitAsync();
            ownsAccess = true;

            var currentUser = _discordClient.CurrentUser;
            if (currentUser == null)
            {
                return;
            }

            foreach (var control in Controls)
            {
                if (_shutdownToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await _discordOperations.RunAsync(
                        "remove expired control",
                        options => session.Message.RemoveReactionAsync(
                            control,
                            currentUser,
                            options),
                        expirationCancellation);
                }
                catch (OperationCanceledException) when (expirationCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    BeanBotLog.PaginatorControlRemoveFailed(_logger, messageId, exception);
                }
            }
        }
        catch (OperationCanceledException) when (expirationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            BeanBotLog.PaginatorExpirationFailed(_logger, messageId, exception);
        }
        finally
        {
            if (ownsCompletion)
            {
                if (ownsAccess)
                {
                    session.Access.Release();
                }
                CompleteSession(messageId, session);
            }
        }
    }

    private async Task StopSessionAsync(
        ulong messageId,
        PaginationSession session,
        bool deleteMessage)
    {
        if (!session.TryBeginCompletion())
        {
            await session.CompletionTask;
            return;
        }

        session.CancelExpiration();
        try
        {
            await session.ExpirationTask;
            if (deleteMessage && !_shutdownToken.IsCancellationRequested)
            {
                await _discordOperations.RunAsync(
                    "delete stopped paginator",
                    options => session.Message.DeleteAsync(options),
                    _shutdownToken);
            }
        }
        finally
        {
            CompleteSession(messageId, session);
        }
    }

    internal async Task TryRemoveUserReactionAsync(
        IUserMessage message,
        IEmote emote,
        ulong userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _discordOperations.RunAsync(
                "remove user reaction",
                options => message.RemoveReactionAsync(
                    emote,
                    userId,
                    options),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested
                || _shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.PaginatorUserReactionRemoveFailed(_logger, userId, exception);
        }
    }

    private bool RemoveSession(ulong messageId, PaginationSession session)
        => ((ICollection<KeyValuePair<ulong, PaginationSession>>)_sessions)
            .Remove(new KeyValuePair<ulong, PaginationSession>(messageId, session));

    private void CompleteSession(ulong messageId, PaginationSession session)
    {
        try
        {
            RemoveSession(messageId, session);
            ReleaseAvailableSlot(session);
            session.Dispose();
        }
        finally
        {
            session.MarkCompletionFinished();
        }
    }

    private void ReleaseAvailableSlot(PaginationSession? session = null)
    {
        lock (_syncRoot)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                if (session == null)
                {
                    _availableSlots.Release();
                }
                else
                {
                    session.ReleaseSlot(_availableSlots);
                }
            }
        }
    }

    private static PaginationAction GetAction(IEmote emote)
    {
        if (emote.Equals(FirstPage)) return PaginationAction.First;
        if (emote.Equals(PreviousPage)) return PaginationAction.Previous;
        if (emote.Equals(NextPage)) return PaginationAction.Next;
        if (emote.Equals(LastPage)) return PaginationAction.Last;
        if (emote.Equals(Stop)) return PaginationAction.Stop;
        return PaginationAction.None;
    }

    private static Embed BuildEmbed(IReadOnlyList<string> pages, PaginationCursor cursor)
        => new EmbedBuilder()
            .WithDescription(pages[cursor.PageIndex])
            .WithFooter($"Page {cursor.PageIndex + 1}/{cursor.PageCount}")
            .Build();

    public void Dispose()
    {
        List<Task> shutdownTasks;
        lock (_syncRoot)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _discordClient.ReactionAdded -= HandleReactionAsync;
            _shutdown.Cancel();
            shutdownTasks = new List<Task>(_sessions.Count + 1)
            {
                _discordOperations.StopAsync()
            };
            foreach (var session in _sessions)
            {
                session.Value.CancelExpiration();
                if (session.Value.TryBeginCompletion())
                {
                    shutdownTasks.Add(CompleteOwnedSessionAsync(session.Key, session.Value));
                }
                else
                {
                    shutdownTasks.Add(session.Value.CompletionTask);
                }
            }

            _availableSlots.Dispose();
        }

        var shutdown = Task.WhenAll(shutdownTasks);
        try
        {
            if (WaitForShutdown(shutdown, ShutdownTimeout))
            {
                _shutdown.Dispose();
                return;
            }

            BeanBotLog.PaginatorShutdownTimedOut(_logger, ShutdownTimeout);
        }
        catch (Exception exception)
        {
            BeanBotLog.PaginatorShutdownFailed(_logger, exception);
            _shutdown.Dispose();
            return;
        }

        _ = ObserveDeferredShutdownAsync(shutdown);
    }

    internal static bool WaitForShutdown(Task shutdown, TimeSpan timeout)
        => shutdown.Wait(timeout);

    private async Task CompleteOwnedSessionAsync(
        ulong messageId,
        PaginationSession session)
    {
        await session.ExpirationTask;
        await session.Access.WaitAsync();
        try
        {
            CompleteSession(messageId, session);
        }
        finally
        {
            session.Access.Release();
        }
    }

    private async Task ObserveDeferredShutdownAsync(Task shutdown)
    {
        try
        {
            await shutdown;
        }
        catch (Exception exception)
        {
            BeanBotLog.PaginatorDeferredCleanupFailed(_logger, exception);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class PaginationSession
    {
        public PaginationSession(
            IUserMessage message,
            ulong userId,
            IReadOnlyList<string> pages,
            PaginationCursor cursor,
            CancellationToken shutdownCancellation)
        {
            Message = message;
            UserId = userId;
            Pages = pages;
            Cursor = cursor;
            Lifetime = new PaginatorSessionLifetime(shutdownCancellation);
        }

        public IUserMessage Message { get; }
        public ulong UserId { get; }
        public IReadOnlyList<string> Pages { get; }
        public PaginationCursor Cursor { get; }
        public SemaphoreSlim Access { get; } = new(1, 1);
        public PaginatorSessionLifetime Lifetime { get; }
        public Task ExpirationTask
        {
            get => Lifetime.ExpirationTask;
            set => Lifetime.ExpirationTask = value;
        }
        public CancellationToken ExpirationCancellation => Lifetime.ExpirationCancellation;
        public bool CompletionStarted => Lifetime.CompletionStarted;
        public Task CompletionTask => Lifetime.CompletionTask;

        public bool TryBeginCompletion()
            => Lifetime.TryBeginCompletion();

        public void CancelExpiration() => Lifetime.CancelExpiration();

        public void ReleaseSlot(SemaphoreSlim availableSlots)
            => Lifetime.ReleaseSlot(availableSlots);

        public void MarkCompletionFinished() => Lifetime.MarkCompletionFinished();

        public void Dispose()
        {
            Lifetime.Dispose();
        }
    }
}

internal sealed class PaginatorSessionLifetime : IDisposable
{
    private readonly CancellationTokenSource _expiration;
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completionStarted;
    private int _slotReleased;

    public PaginatorSessionLifetime(CancellationToken shutdownCancellation)
    {
        _expiration = CancellationTokenSource.CreateLinkedTokenSource(shutdownCancellation);
    }

    public Task ExpirationTask { get; set; } = Task.CompletedTask;
    public CancellationToken ExpirationCancellation => _expiration.Token;
    public bool CompletionStarted => Volatile.Read(ref _completionStarted) != 0;
    public Task CompletionTask => _completion.Task;

    public bool TryBeginCompletion()
        => Interlocked.CompareExchange(ref _completionStarted, 1, 0) == 0;

    public void CancelExpiration() => _expiration.Cancel();

    public void MarkCompletionFinished() => _completion.TrySetResult();

    public void ReleaseSlot(SemaphoreSlim availableSlots)
    {
        if (Interlocked.Exchange(ref _slotReleased, 1) == 0)
        {
            availableSlots.Release();
        }
    }

    public void Dispose() => _expiration.Dispose();
}

internal enum PaginationAction
{
    None,
    First,
    Previous,
    Next,
    Last,
    Stop
}

internal sealed class PaginationCursor
{
    public PaginationCursor(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);

        PageCount = pageCount;
    }

    public int PageCount { get; }
    public int PageIndex { get; private set; }

    public bool Move(PaginationAction action)
    {
        var previousPage = PageIndex;
        switch (action)
        {
            case PaginationAction.First:
                PageIndex = 0;
                break;
            case PaginationAction.Previous when PageIndex > 0:
                PageIndex--;
                break;
            case PaginationAction.Next when PageIndex < PageCount - 1:
                PageIndex++;
                break;
            case PaginationAction.Last:
                PageIndex = PageCount - 1;
                break;
        }

        return previousPage != PageIndex;
    }
}
