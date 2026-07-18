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
    internal sealed class DiscordOwnerErrorSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level >= LogEventLevel.Error || logEvent.Exception != null)
            {
                DiscordOwnerErrorNotifier.Enqueue(FormatAlert(logEvent));
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

    internal static class DiscordOwnerErrorNotifier
    {
        private static readonly Channel<string> Alerts = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        private static readonly CancellationTokenSource Shutdown = new CancellationTokenSource();
        private static readonly object Sync = new object();
        private static DiscordSocketClient _discordClient;
        private static Task _worker;
        private static int _disposed;
        private static int _sendInProgress;

        public static void Initialize(DiscordSocketClient discordClient)
        {
            if (discordClient == null)
            {
                throw new ArgumentNullException(nameof(discordClient));
            }

            lock (Sync)
            {
                _discordClient = discordClient;
                _worker ??= Task.Run(() => ProcessAlertsAsync(Shutdown.Token));
            }
        }

        public static void Enqueue(string alert)
        {
            Alerts.Writer.TryWrite(alert);
        }

        public static async Task FlushAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while ((Alerts.Reader.Count > 0 || Volatile.Read(ref _sendInProgress) != 0) &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }

        public static async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Alerts.Writer.TryComplete();
            Task worker;
            lock (Sync)
            {
                worker = _worker;
            }

            if (worker != null)
            {
                var completed = await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(3)));
                if (completed != worker)
                {
                    Shutdown.Cancel();
                    completed = await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(1)));
                }

                if (completed == worker)
                {
                    try
                    {
                        await worker;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }

            if (worker?.IsCompleted != false)
            {
                Shutdown.Dispose();
            }
        }

        private static async Task ProcessAlertsAsync(CancellationToken cancellationToken)
        {
            await foreach (var alert in Alerts.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Exchange(ref _sendInProgress, 1);
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            if (_discordClient?.LoginState != LoginState.LoggedIn)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                                continue;
                            }

                            var owner = _discordClient.GetUser(BotOwner.DiscordUserId) ??
                                await ((IDiscordClient)_discordClient).GetUserAsync(BotOwner.DiscordUserId, CacheMode.AllowDownload);
                            if (owner == null)
                            {
                                throw new InvalidOperationException($"Discord user {BotOwner.DiscordUserId} could not be found.");
                            }

                            var directMessageChannel = await owner.GetOrCreateDMChannelAsync();
                            await directMessageChannel.SendMessageAsync(alert);
                            break;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            // Never log from here: doing so would enqueue another alert recursively.
                            Console.Error.WriteLine($"Could not DM BeanBot error to its owner: {exception.Message}");
                            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _sendInProgress, 0);
                }
            }
        }
    }
}
