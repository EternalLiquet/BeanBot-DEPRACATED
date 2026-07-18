using Discord;
using Discord.WebSocket;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BeanBot.Util
{
    internal interface IOwnerErrorNotifier
    {
        void Enqueue(string alert);
    }

    internal interface IOwnerAlertDelivery
    {
        Task DeliverAsync(string alert, CancellationToken cancellationToken);
    }

    internal sealed class DiscordOwnerErrorSink : ILogEventSink
    {
        private readonly IOwnerErrorNotifier _notifier;

        public DiscordOwnerErrorSink(IOwnerErrorNotifier notifier)
        {
            _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        }

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level >= LogEventLevel.Error)
            {
                _notifier.Enqueue(FormatAlert(logEvent));
            }
        }

        internal static string FormatAlert(LogEvent logEvent)
        {
            const int maximumLength = 1900;
            var exception = logEvent.Exception == null ? string.Empty : $"\n{logEvent.Exception}";
            var alert = $"BeanBot {logEvent.Level} at {logEvent.Timestamp:O}\n{logEvent.RenderMessage()}{exception}";
            return alert.Length <= maximumLength
                ? alert
                : alert.Substring(0, maximumLength - 15) + "\n...(truncated)";
        }
    }

    internal sealed class DiscordOwnerErrorNotifier : IOwnerErrorNotifier, IAsyncDisposable
    {
        internal const int DefaultMaximumAttempts = 3;
        private readonly Channel<string> _alerts;
        private readonly IOwnerAlertDelivery _delivery;
        private readonly Func<int, TimeSpan> _retryDelay;
        private readonly TimeSpan _shutdownFlushTimeout;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly Task _worker;
        private int _sendInProgress;
        private int _disposed;

        public DiscordOwnerErrorNotifier(DiscordSocketClient discordClient)
            : this(new DiscordOwnerAlertDelivery(discordClient))
        {
        }

        internal DiscordOwnerErrorNotifier(
            IOwnerAlertDelivery delivery,
            Func<int, TimeSpan> retryDelay = null,
            TimeSpan? shutdownFlushTimeout = null)
        {
            _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
            _retryDelay = retryDelay ?? (attempt => TimeSpan.FromSeconds(attempt));
            _shutdownFlushTimeout = shutdownFlushTimeout ?? TimeSpan.FromSeconds(3);
            _alerts = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _worker = Task.Run(() => ProcessAlertsAsync(_shutdown.Token));
        }

        public void Enqueue(string alert)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _alerts.Writer.TryWrite(alert);
            }
        }

        public async Task FlushAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while ((_alerts.Reader.Count > 0 || Volatile.Read(ref _sendInProgress) != 0) &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _alerts.Writer.TryComplete();
            await FlushAsync(_shutdownFlushTimeout);
            _shutdown.Cancel();

            try
            {
                await _worker;
            }
            catch (OperationCanceledException)
            {
            }

            _shutdown.Dispose();
        }

        private async Task ProcessAlertsAsync(CancellationToken cancellationToken)
        {
            await foreach (var alert in _alerts.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Exchange(ref _sendInProgress, 1);
                try
                {
                    await DeliverWithRetryAsync(alert, cancellationToken);
                }
                finally
                {
                    Interlocked.Exchange(ref _sendInProgress, 0);
                }
            }
        }

        private async Task DeliverWithRetryAsync(string alert, CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= DefaultMaximumAttempts; attempt++)
            {
                try
                {
                    // Discord.Net does not expose cancellation on every DM operation.
                    // WaitAsync still guarantees the notifier worker and application
                    // shutdown are not held hostage by a stalled network task.
                    await _delivery.DeliverAsync(alert, cancellationToken).WaitAsync(cancellationToken);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (attempt == DefaultMaximumAttempts)
                    {
                        // Never use Serilog here: that would recursively enqueue another owner alert.
                        Console.Error.WriteLine(
                            $"Could not DM BeanBot error to its owner after {attempt} attempts: {exception.Message}");
                        return;
                    }

                    await Task.Delay(_retryDelay(attempt), cancellationToken);
                }
            }
        }
    }

    internal sealed class DiscordOwnerAlertDelivery : IOwnerAlertDelivery
    {
        private readonly DiscordSocketClient _discordClient;

        public DiscordOwnerAlertDelivery(DiscordSocketClient discordClient)
        {
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        }

        public async Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_discordClient.LoginState != LoginState.LoggedIn)
            {
                throw new InvalidOperationException("Discord is not logged in, so the owner alert cannot be delivered yet.");
            }

            var owner = _discordClient.GetUser(BotOwner.DiscordUserId) ??
                await ((IDiscordClient)_discordClient).GetUserAsync(BotOwner.DiscordUserId, CacheMode.AllowDownload);
            if (owner == null)
            {
                throw new InvalidOperationException($"Discord user {BotOwner.DiscordUserId} could not be found.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var directMessageChannel = await owner.GetOrCreateDMChannelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            await directMessageChannel.SendMessageAsync(alert);
        }
    }
}
