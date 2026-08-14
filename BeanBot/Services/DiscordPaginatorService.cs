using Discord;
using Discord.Commands;
using Discord.WebSocket;

using Serilog;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    public sealed class DiscordPaginatorService : IDisposable
    {
        internal const int MaximumActivePaginators = 64;
        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
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
        private readonly object _syncRoot = new();
        private int _disposed;

        public DiscordPaginatorService(DiscordSocketClient discordClient)
        {
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
            _discordClient.ReactionAdded += HandleReactionAsync;
        }

        public async Task<IUserMessage> SendAsync(
            SocketCommandContext context,
            IReadOnlyCollection<string> pages,
            TimeSpan? timeout = null)
        {
            ThrowIfDisposed();
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

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

            if (!_availableSlots.Wait(0))
            {
                throw new InvalidOperationException("Too many Discord paginators are already active.");
            }

            IUserMessage message = null;
            PaginationSession session = null;
            var sessionRegistered = false;
            var sessionAccessHeld = false;
            try
            {
                var cursor = new PaginationCursor(pageList.Count);
                message = await context.Channel.SendMessageAsync(embed: BuildEmbed(pageList, cursor));
                session = new PaginationSession(message, context.User.Id, pageList, cursor, _shutdown.Token);
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
                    await message.AddReactionAsync(control);
                }

                if (!_sessions.TryGetValue(message.Id, out var currentSession)
                    || !ReferenceEquals(currentSession, session)
                    || session.CompletionStarted)
                {
                    throw new ObjectDisposedException(nameof(DiscordPaginatorService));
                }

                return message;
            }
            catch
            {
                if (!sessionRegistered)
                {
                    _availableSlots.Release();
                }
                else
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
                    session.Access.Release();
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

            try
            {
                var stopRequested = false;
                await session.Access.WaitAsync(_shutdown.Token);
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
                            await session.Message.ModifyAsync(
                                properties => properties.Embed = BuildEmbed(session.Pages, session.Cursor));
                        }

                        await TryRemoveUserReactionAsync(
                            session.Message,
                            reaction.Emote,
                            reaction.UserId);
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
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Log.Warning(
                    exception,
                    "Could not process Discord paginator reaction for message {MessageId}",
                    message.Id);
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
                    if (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        await session.Message.RemoveReactionAsync(control, currentUser);
                    }
                    catch (Exception exception)
                    {
                        Log.Debug(
                            exception,
                            "Could not remove expired paginator control from message {MessageId}",
                            messageId);
                    }
                }
            }
            catch (OperationCanceledException) when (expirationCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Discord paginator expiration failed for message {MessageId}", messageId);
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
                if (deleteMessage && !_shutdown.IsCancellationRequested)
                {
                    await session.Message.DeleteAsync();
                }
            }
            finally
            {
                CompleteSession(messageId, session);
            }
        }

        internal static async Task TryRemoveUserReactionAsync(
            IUserMessage message,
            IEmote emote,
            ulong userId)
        {
            try
            {
                await message.RemoveReactionAsync(emote, userId);
            }
            catch (Exception exception)
            {
                Log.Debug(
                    exception,
                    "Could not remove paginator reaction from user {UserId}",
                    userId);
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
                session.ReleaseSlot(_availableSlots);
                session.Dispose();
            }
            finally
            {
                session.MarkCompletionFinished();
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
            List<Task> inFlightCompletions;
            List<KeyValuePair<ulong, PaginationSession>> ownedSessions;
            lock (_syncRoot)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                _discordClient.ReactionAdded -= HandleReactionAsync;
                _shutdown.Cancel();
                inFlightCompletions = new List<Task>(_sessions.Count);
                ownedSessions = new List<KeyValuePair<ulong, PaginationSession>>(_sessions.Count);
                foreach (var session in _sessions)
                {
                    session.Value.CancelExpiration();
                    if (session.Value.TryBeginCompletion())
                    {
                        ownedSessions.Add(session);
                    }
                    else
                    {
                        inFlightCompletions.Add(session.Value.CompletionTask);
                    }
                }
            }

            foreach (var session in ownedSessions)
            {
                session.Value.ExpirationTask.GetAwaiter().GetResult();
                session.Value.Access.Wait();
                try
                {
                    CompleteSession(session.Key, session.Value);
                }
                finally
                {
                    session.Value.Access.Release();
                }
            }
            Task.WhenAll(inFlightCompletions).GetAwaiter().GetResult();
            _shutdown.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(DiscordPaginatorService));
            }
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
            if (pageCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageCount));
            }

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
}
