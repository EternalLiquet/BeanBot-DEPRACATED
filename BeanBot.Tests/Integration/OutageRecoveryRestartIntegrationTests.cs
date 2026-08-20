using BeanBot.Discord.Lifecycle;
using BeanBot.Logging;
using BeanBot.Persistence.Outages;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace BeanBot.Tests.Integration;

public sealed class OutageRecoveryRestartIntegrationTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"beanbot-outage-restart-tests-{Guid.NewGuid():N}");

    public OutageRecoveryRestartIntegrationTests()
        => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public async Task PersistedOutage_SurvivesRestartsAndClearsOnlyAfterSuccessfulNotification()
    {
        var disconnectedAtUtc = new DateTimeOffset(2026, 8, 19, 7, 42, 15, TimeSpan.Zero);
        using (var writer = CreateStore())
        {
            await writer.OpenAsync(disconnectedAtUtc, "Gateway timed out");
            await writer.MarkManualRecoveryAttemptedAsync(disconnectedAtUtc, "Manual reconnect failed");
            await writer.MarkProcessRestartRequestedAsync(disconnectedAtUtc, "Ready timeout");
        }

        var outagePath = Path.Combine(_temporaryDirectory, DiscordOutageStore.OutageFileName);
        var failedDelivery = new RecordingDelivery(alwaysFail: true);
        using (var restartedStore = CreateStore())
        using (var failedNotifier = CreateNotifier(restartedStore, failedDelivery))
        {
            await failedNotifier.NotifyIfOutageRecoveredAsync(disconnectedAtUtc.AddMinutes(10));

            var retainedOutage = await restartedStore.ReadAsync();
            Assert.NotNull(retainedOutage);
            Assert.True(retainedOutage.ManualRecoveryAttempted);
            Assert.True(retainedOutage.ProcessRestartRequested);
            Assert.Equal("Ready timeout", retainedOutage.MostRecentDisconnectReason);
            Assert.True(File.Exists(outagePath));
            Assert.Equal(
                DiscordOutageRecoveryNotifier.MaximumDeliveryAttempts,
                failedDelivery.AttemptCount);
        }

        var successfulDelivery = new RecordingDelivery();
        using (var recoveredStore = CreateStore())
        using (var successfulNotifier = CreateNotifier(recoveredStore, successfulDelivery))
        {
            await successfulNotifier.NotifyIfOutageRecoveredAsync(disconnectedAtUtc.AddMinutes(12));

            var message = Assert.Single(successfulDelivery.Messages);
            Assert.Contains("Container restart requested: yes", message);
            Assert.Contains("persisted across a requested process/container restart", message);
            Assert.Null(await recoveredStore.ReadAsync());
            Assert.False(File.Exists(outagePath));
        }
    }

    private DiscordOutageStore CreateStore()
        => new(_temporaryDirectory, NullLogger<DiscordOutageStore>.Instance);

    private static DiscordOutageRecoveryNotifier CreateNotifier(
        IDiscordOutageStore store,
        RecordingDelivery delivery)
        => new(
            store,
            delivery,
            NullLogger<DiscordOutageRecoveryNotifier>.Instance,
            _ => TimeSpan.Zero,
            TimeSpan.FromSeconds(1));

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class RecordingDelivery : IOwnerAlertDelivery
    {
        private readonly bool _alwaysFail;

        public RecordingDelivery(bool alwaysFail = false) => _alwaysFail = alwaysFail;

        public List<string> Messages { get; } = [];
        public int AttemptCount { get; private set; }

        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
