using BeanBot.Util;

using Microsoft.Extensions.Logging;

using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    internal sealed class DiscordOutageRecoveryNotifier : IDisposable
    {
        internal const int MaximumDeliveryAttempts = 3;
        private const int MaximumDiscordMessageLength = 1900;
        private static readonly TimeSpan DefaultDeliveryTimeout = TimeSpan.FromSeconds(30);
        private readonly IDiscordOutageStore _outageStore;
        private readonly IOwnerAlertDelivery _ownerAlertDelivery;
        private readonly Func<int, TimeSpan> _retryDelay;
        private readonly TimeSpan _deliveryTimeout;
        private readonly ILogger<DiscordOutageRecoveryNotifier> _logger;
        private readonly SemaphoreSlim _notificationAccess = new(1, 1);

        public DiscordOutageRecoveryNotifier(
            IDiscordOutageStore outageStore,
            IOwnerAlertDelivery ownerAlertDelivery,
            ILogger<DiscordOutageRecoveryNotifier> logger,
            Func<int, TimeSpan>? retryDelay = null,
            TimeSpan? deliveryTimeout = null)
        {
            _outageStore = outageStore ?? throw new ArgumentNullException(nameof(outageStore));
            _ownerAlertDelivery = ownerAlertDelivery ?? throw new ArgumentNullException(nameof(ownerAlertDelivery));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryDelay = retryDelay ?? (attempt => TimeSpan.FromSeconds(attempt));
            _deliveryTimeout = deliveryTimeout ?? DefaultDeliveryTimeout;
        }

        public async Task NotifyIfOutageRecoveredAsync(
            DateTimeOffset recoveredAtUtc,
            CancellationToken cancellationToken = default)
        {
            await _notificationAccess.WaitAsync(cancellationToken);
            try
            {
                var outage = await _outageStore.ReadAsync(cancellationToken);
                if (outage is null)
                {
                    return;
                }

                BeanBotLog.OutageNotificationAttempting(
                    _logger,
                    outage.DisconnectedAtUtc,
                    outage.ProcessRestartRequested);

                var recoveryMessage = FormatRecoveryMessage(outage, recoveredAtUtc);
                if (!await DeliverWithRetryAsync(recoveryMessage, cancellationToken))
                {
                    BeanBotLog.OutageNotificationFailed(
                        _logger,
                        MaximumDeliveryAttempts,
                        outage.DisconnectedAtUtc);
                    return;
                }

                BeanBotLog.OutageNotificationDelivered(
                    _logger,
                    outage.DisconnectedAtUtc);
                await _outageStore.ClearAsync(cancellationToken);
            }
            finally
            {
                _notificationAccess.Release();
            }
        }

        internal static string FormatRecoveryMessage(
            DiscordOutage outage,
            DateTimeOffset recoveredAtUtc)
        {
            var downtime = recoveredAtUtc.ToUniversalTime() - outage.DisconnectedAtUtc.ToUniversalTime();
            if (downtime < TimeSpan.Zero)
            {
                downtime = TimeSpan.Zero;
            }

            var message = new StringBuilder()
                .AppendLine("BeanBot recovered from a Discord outage.")
                .AppendLine();
            message.AppendLine(
                CultureInfo.InvariantCulture,
                $"Disconnected: {outage.DisconnectedAtUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");
            message.AppendLine(
                CultureInfo.InvariantCulture,
                $"Approximate downtime: {FormatDuration(downtime)}");
            message.AppendLine(CultureInfo.InvariantCulture, $"Reason: {outage.MostRecentDisconnectReason}");
            message.AppendLine(
                CultureInfo.InvariantCulture,
                $"Manual recovery attempted: {FormatBoolean(outage.ManualRecoveryAttempted)}");
            message.Append(
                CultureInfo.InvariantCulture,
                $"Container restart requested: {FormatBoolean(outage.ProcessRestartRequested)}");

            if (outage.ProcessRestartRequested)
            {
                message.AppendLine().Append("This outage persisted across a requested process/container restart.");
            }

            var recoveryMessage = message.ToString();
            return recoveryMessage.Length <= MaximumDiscordMessageLength
                ? recoveryMessage
                : string.Concat(
                    recoveryMessage.AsSpan(0, MaximumDiscordMessageLength - 15),
                    "\n...(truncated)");
        }

        private async Task<bool> DeliverWithRetryAsync(
            string recoveryMessage,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaximumDeliveryAttempts; attempt++)
            {
                try
                {
                    await _ownerAlertDelivery
                        .DeliverAsync(recoveryMessage, cancellationToken)
                        .WaitAsync(_deliveryTimeout, cancellationToken);
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    BeanBotLog.OutageNotificationDeliveryFailed(
                        _logger,
                        attempt,
                        MaximumDeliveryAttempts,
                        exception);

                    if (attempt < MaximumDeliveryAttempts)
                    {
                        await Task.Delay(_retryDelay(attempt), cancellationToken);
                    }
                }
            }

            return false;
        }

        private static string FormatDuration(TimeSpan duration)
        {
            var parts = new StringBuilder();
            if (duration.Days > 0)
            {
                parts.Append(CultureInfo.InvariantCulture, $"{duration.Days}d ");
            }
            if (duration.Hours > 0 || duration.Days > 0)
            {
                parts.Append(CultureInfo.InvariantCulture, $"{duration.Hours}h ");
            }
            if (duration.Minutes > 0 || duration.Hours > 0 || duration.Days > 0)
            {
                parts.Append(CultureInfo.InvariantCulture, $"{duration.Minutes}m ");
            }
            parts.Append(CultureInfo.InvariantCulture, $"{duration.Seconds}s");
            return parts.ToString();
        }

        private static string FormatBoolean(bool value) => value ? "yes" : "no";

        public void Dispose() => _notificationAccess.Dispose();
    }
}
