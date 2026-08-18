using BeanBot.Services;
using BeanBot.Util;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace BeanBot.Tests.Services;

public class DiscordOutageRecoveryNotifierTests
{
    [Fact]
    public async Task NoPersistedOutage_DoesNotDeliverNotification()
    {
        var store = new InMemoryOutageStore();
        var delivery = new RecordingDelivery();
        using var notifier = CreateNotifier(store, delivery);

        await notifier.NotifyIfOutageRecoveredAsync(DateTimeOffset.UtcNow);

        Assert.Empty(delivery.Messages);
    }

    [Fact]
    public async Task PersistedOutage_DeliversNotificationAndClearsOutage()
    {
        var outage = CreateOutage();
        var store = new InMemoryOutageStore(outage);
        var delivery = new RecordingDelivery();
        using var notifier = CreateNotifier(store, delivery);

        await notifier.NotifyIfOutageRecoveredAsync(outage.DisconnectedAtUtc.AddMinutes(8).AddSeconds(13));

        var message = Assert.Single(delivery.Messages);
        Assert.Contains("BeanBot recovered from a Discord outage", message);
        Assert.Contains("2026-08-12 07:42:15 UTC", message);
        Assert.Contains("8m 13s", message);
        Assert.Contains("Gateway timed out", message);
        Assert.Null(store.CurrentOutage);
    }

    [Fact]
    public async Task FailedDelivery_RetainsPersistedOutage()
    {
        var store = new InMemoryOutageStore(CreateOutage());
        var delivery = new RecordingDelivery(alwaysFail: true);
        using var notifier = CreateNotifier(store, delivery);

        await notifier.NotifyIfOutageRecoveredAsync(DateTimeOffset.UtcNow);

        Assert.Equal(DiscordOutageRecoveryNotifier.MaximumDeliveryAttempts, delivery.AttemptCount);
        Assert.NotNull(store.CurrentOutage);
    }

    [Fact]
    public async Task RepeatedReadyEvents_DoNotDuplicateDeliveredNotification()
    {
        var store = new InMemoryOutageStore(CreateOutage());
        var delivery = new RecordingDelivery();
        using var notifier = CreateNotifier(store, delivery);

        await Task.WhenAll(
            notifier.NotifyIfOutageRecoveredAsync(DateTimeOffset.UtcNow),
            notifier.NotifyIfOutageRecoveredAsync(DateTimeOffset.UtcNow));

        Assert.Single(delivery.Messages);
    }

    [Fact]
    public async Task RestartedProcessOutage_IsClearlyReported()
    {
        var outage = CreateOutage();
        outage.ProcessRestartRequested = true;
        var store = new InMemoryOutageStore(outage);
        var delivery = new RecordingDelivery();
        using var notifier = CreateNotifier(store, delivery);

        await notifier.NotifyIfOutageRecoveredAsync(DateTimeOffset.UtcNow);

        var message = Assert.Single(delivery.Messages);
        Assert.Contains("Container restart requested: yes", message);
        Assert.Contains("persisted across a requested process/container restart", message);
    }

    private static DiscordOutageRecoveryNotifier CreateNotifier(
        InMemoryOutageStore store,
        RecordingDelivery delivery)
        => new(
            store,
            delivery,
            NullLogger<DiscordOutageRecoveryNotifier>.Instance,
            _ => TimeSpan.Zero);

    private static DiscordOutage CreateOutage()
        => new()
        {
            DisconnectedAtUtc = new DateTimeOffset(2026, 8, 12, 7, 42, 15, TimeSpan.Zero),
            MostRecentDisconnectReason = "Gateway timed out",
            ManualRecoveryAttempted = true
        };

    private sealed class InMemoryOutageStore : IDiscordOutageStore
    {
        public InMemoryOutageStore(DiscordOutage? outage = null) => CurrentOutage = outage;
        public DiscordOutage? CurrentOutage { get; private set; }

        public Task<DiscordOutage?> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentOutage);

        public Task OpenAsync(DateTimeOffset disconnectedAtUtc, string? reason, CancellationToken cancellationToken = default)
        {
            CurrentOutage ??= new DiscordOutage
            {
                DisconnectedAtUtc = disconnectedAtUtc,
                MostRecentDisconnectReason = reason ?? "Discord gateway disconnected."
            };
            return Task.CompletedTask;
        }

        public Task MarkManualRecoveryAttemptedAsync(DateTimeOffset disconnectedAtUtc, string? reason, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkProcessRestartRequestedAsync(DateTimeOffset disconnectedAtUtc, string? reason, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            CurrentOutage = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDelivery : IOwnerAlertDelivery
    {
        private readonly bool _alwaysFail;
        public RecordingDelivery(bool alwaysFail = false) => _alwaysFail = alwaysFail;
        public List<string> Messages { get; } = new();
        public int AttemptCount { get; private set; }

        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            AttemptCount++;
            if (_alwaysFail)
            {
                throw new InvalidOperationException("Discord REST unavailable");
            }

            Messages.Add(alert);
            return Task.CompletedTask;
        }
    }
}
