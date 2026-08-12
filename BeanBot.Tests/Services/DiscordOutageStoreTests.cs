using BeanBot.Services;

using Xunit;

namespace BeanBot.Tests.Services;

public sealed class DiscordOutageStoreTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"beanbot-outage-tests-{Guid.NewGuid():N}");

    public DiscordOutageStoreTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public async Task OpenAsync_PersistsOutageStartAndReason()
    {
        using var store = new DiscordOutageStore(_temporaryDirectory);
        var disconnectedAtUtc = new DateTimeOffset(2026, 8, 12, 7, 42, 15, TimeSpan.Zero);

        await store.OpenAsync(disconnectedAtUtc, "Gateway timed out");
        var outage = await store.ReadAsync();

        Assert.NotNull(outage);
        Assert.Equal(disconnectedAtUtc, outage.DisconnectedAtUtc);
        Assert.Equal("Gateway timed out", outage.MostRecentDisconnectReason);
    }

    [Fact]
    public async Task RepeatedOpenAsync_PreservesOriginalOutageStart()
    {
        using var store = new DiscordOutageStore(_temporaryDirectory);
        var originalDisconnect = new DateTimeOffset(2026, 8, 12, 7, 42, 15, TimeSpan.Zero);

        await store.OpenAsync(originalDisconnect, "First disconnect");
        await store.OpenAsync(originalDisconnect.AddMinutes(3), "Later disconnect");
        var outage = await store.ReadAsync();

        Assert.Equal(originalDisconnect, outage!.DisconnectedAtUtc);
        Assert.Equal("Later disconnect", outage.MostRecentDisconnectReason);
    }

    [Fact]
    public async Task MarkManualRecoveryAttemptedAsync_OpensAndUpdatesOutage()
    {
        using var store = new DiscordOutageStore(_temporaryDirectory);
        var disconnectedAtUtc = new DateTimeOffset(2026, 8, 12, 7, 42, 15, TimeSpan.Zero);

        await store.OpenAsync(disconnectedAtUtc, "Initial disconnect");
        await store.MarkManualRecoveryAttemptedAsync(disconnectedAtUtc.AddMinutes(5), "DNS failure");
        var outage = await store.ReadAsync();

        Assert.True(outage!.ManualRecoveryAttempted);
        Assert.False(outage.ProcessRestartRequested);
        Assert.Equal(disconnectedAtUtc, outage.DisconnectedAtUtc);
        Assert.Equal("DNS failure", outage.MostRecentDisconnectReason);
    }

    [Fact]
    public async Task MarkProcessRestartRequestedAsync_UpdatesExistingOutage()
    {
        using var store = new DiscordOutageStore(_temporaryDirectory);
        await store.MarkManualRecoveryAttemptedAsync(DateTimeOffset.UtcNow, "Initial reason");

        await store.MarkProcessRestartRequestedAsync(DateTimeOffset.UtcNow, "Ready timeout");
        var outage = await store.ReadAsync();

        Assert.True(outage!.ProcessRestartRequested);
        Assert.Equal("Ready timeout", outage.MostRecentDisconnectReason);
    }

    [Fact]
    public async Task ClearAsync_RemovesPersistedOutage()
    {
        using var store = new DiscordOutageStore(_temporaryDirectory);
        await store.OpenAsync(DateTimeOffset.UtcNow, "Disconnect");

        await store.ClearAsync();

        Assert.Null(await store.ReadAsync());
        Assert.False(File.Exists(Path.Combine(_temporaryDirectory, DiscordOutageStore.OutageFileName)));
    }

    [Fact]
    public async Task ReadAsync_QuarantinesMalformedJsonWithoutThrowing()
    {
        var outagePath = Path.Combine(_temporaryDirectory, DiscordOutageStore.OutageFileName);
        await File.WriteAllTextAsync(outagePath, "{ definitely not valid JSON");
        using var store = new DiscordOutageStore(_temporaryDirectory);

        var outage = await store.ReadAsync();

        Assert.Null(outage);
        Assert.False(File.Exists(outagePath));
        Assert.Single(Directory.GetFiles(_temporaryDirectory, "*.corrupt-*"));
    }

    [Fact]
    public async Task WriteAsync_LeavesCompleteFinalFileAndNoTemporaryFile()
    {
        using var store = new DiscordOutageStore(_temporaryDirectory);

        await store.OpenAsync(DateTimeOffset.UtcNow, "Disconnect");

        Assert.True(File.Exists(Path.Combine(_temporaryDirectory, DiscordOutageStore.OutageFileName)));
        Assert.Empty(Directory.GetFiles(_temporaryDirectory, "*.tmp"));
        Assert.NotNull(await store.ReadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
