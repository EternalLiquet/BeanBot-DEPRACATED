using System.Text.Json;
using BeanBot.Util;
using Microsoft.Extensions.Logging;

namespace BeanBot.Services;

internal sealed class DiscordOutage
{
    public DateTimeOffset DisconnectedAtUtc { get; set; }
    public string MostRecentDisconnectReason { get; set; } = "Discord gateway disconnected.";
    public bool ManualRecoveryAttempted { get; set; }
    public bool ProcessRestartRequested { get; set; }
}

internal interface IDiscordOutageStore
{
    Task<DiscordOutage?> ReadAsync(CancellationToken cancellationToken = default);
    Task OpenAsync(
        DateTimeOffset disconnectedAtUtc,
        string? mostRecentDisconnectReason,
        CancellationToken cancellationToken = default);
    Task MarkManualRecoveryAttemptedAsync(
        DateTimeOffset disconnectedAtUtc,
        string? mostRecentDisconnectReason,
        CancellationToken cancellationToken = default);
    Task MarkProcessRestartRequestedAsync(
        DateTimeOffset disconnectedAtUtc,
        string? mostRecentDisconnectReason,
        CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

internal sealed class DiscordOutageStore : IDiscordOutageStore, IDisposable
{
    internal const string OutageFileName = "discord-outage.json";
    private readonly string _outageFilePath;
    private readonly SemaphoreSlim _fileAccess = new(1, 1);
    private readonly ILogger<DiscordOutageStore> _logger;

    public DiscordOutageStore(
        string persistentDataDirectory,
        ILogger<DiscordOutageStore> logger)
    {
        if (string.IsNullOrWhiteSpace(persistentDataDirectory))
        {
            throw new ArgumentException("A persistent data directory is required.", nameof(persistentDataDirectory));
        }

        Directory.CreateDirectory(persistentDataDirectory);
        _outageFilePath = Path.Combine(persistentDataDirectory, OutageFileName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DiscordOutage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _fileAccess.WaitAsync(cancellationToken);
        try
        {
            return await ReadWithoutLockAsync(cancellationToken);
        }
        finally
        {
            _fileAccess.Release();
        }
    }

    public async Task OpenAsync(
        DateTimeOffset disconnectedAtUtc,
        string? mostRecentDisconnectReason,
        CancellationToken cancellationToken = default)
    {
        await UpdateAsync(
            existingOutage =>
            {
                var outage = existingOutage ?? CreateOutage(disconnectedAtUtc, mostRecentDisconnectReason);
                outage.MostRecentDisconnectReason = NormalizeReason(mostRecentDisconnectReason);
                return outage;
            },
            cancellationToken);
    }

    public async Task MarkManualRecoveryAttemptedAsync(
        DateTimeOffset disconnectedAtUtc,
        string? mostRecentDisconnectReason,
        CancellationToken cancellationToken = default)
    {
        await UpdateAsync(
            existingOutage =>
            {
                var outage = existingOutage ?? CreateOutage(disconnectedAtUtc, mostRecentDisconnectReason);
                outage.MostRecentDisconnectReason = NormalizeReason(mostRecentDisconnectReason);
                outage.ManualRecoveryAttempted = true;
                return outage;
            },
            cancellationToken);

        BeanBotLog.OutageManualRecoveryPersisted(_logger, disconnectedAtUtc);
    }

    public async Task MarkProcessRestartRequestedAsync(
        DateTimeOffset disconnectedAtUtc,
        string? mostRecentDisconnectReason,
        CancellationToken cancellationToken = default)
    {
        await UpdateAsync(
            existingOutage =>
            {
                var outage = existingOutage ?? CreateOutage(disconnectedAtUtc, mostRecentDisconnectReason);
                outage.MostRecentDisconnectReason = NormalizeReason(mostRecentDisconnectReason);
                outage.ProcessRestartRequested = true;
                return outage;
            },
            cancellationToken);

        BeanBotLog.OutageRestartPersisted(_logger);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _fileAccess.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_outageFilePath))
            {
                File.Delete(_outageFilePath);
                BeanBotLog.OutageCleared(_logger);
            }
        }
        finally
        {
            _fileAccess.Release();
        }
    }

    private async Task UpdateAsync(
        Func<DiscordOutage?, DiscordOutage> updateOutage,
        CancellationToken cancellationToken)
    {
        await _fileAccess.WaitAsync(cancellationToken);
        try
        {
            var existingOutage = await ReadWithoutLockAsync(cancellationToken);
            var updatedOutage = updateOutage(existingOutage);
            if (updatedOutage is not null)
            {
                await WriteAtomicallyAsync(updatedOutage, cancellationToken);
            }
        }
        finally
        {
            _fileAccess.Release();
        }
    }

    private async Task<DiscordOutage?> ReadWithoutLockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_outageFilePath))
        {
            return null;
        }

        try
        {
            await using var outageFile = new FileStream(
                _outageFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous);
            var outage = await JsonSerializer.DeserializeAsync<DiscordOutage>(
                outageFile,
                cancellationToken: cancellationToken);
            if (outage is null || outage.DisconnectedAtUtc == default)
            {
                throw new JsonException("The persisted Discord outage is missing required data.");
            }

            outage.MostRecentDisconnectReason = NormalizeReason(outage.MostRecentDisconnectReason);

            BeanBotLog.OutageLoaded(
                _logger,
                outage.DisconnectedAtUtc,
                outage.ManualRecoveryAttempted,
                outage.ProcessRestartRequested);
            return outage;
        }
        catch (JsonException exception)
        {
            QuarantineCorruptOutageFile(exception);
            return null;
        }
    }

    private async Task WriteAtomicallyAsync(
        DiscordOutage outage,
        CancellationToken cancellationToken)
    {
        var temporaryFilePath = $"{_outageFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var temporaryFile = new FileStream(
                temporaryFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    temporaryFile,
                    outage,
                    cancellationToken: cancellationToken);
                await temporaryFile.FlushAsync(cancellationToken);
                temporaryFile.Flush(flushToDisk: true);
            }

            File.Move(temporaryFilePath, _outageFilePath, overwrite: true);
            BeanBotLog.OutagePersisted(
                _logger,
                outage.DisconnectedAtUtc,
                outage.ManualRecoveryAttempted,
                outage.ProcessRestartRequested);
        }
        finally
        {
            if (File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }
        }
    }

    private void QuarantineCorruptOutageFile(JsonException exception)
    {
        var quarantinePath = $"{_outageFilePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            File.Move(_outageFilePath, quarantinePath);
            BeanBotLog.OutageQuarantined(_logger, quarantinePath, exception);
        }
        catch (Exception quarantineException)
        {
            BeanBotLog.OutageQuarantineFailed(_logger, _outageFilePath, quarantineException);
        }
    }

    private static DiscordOutage CreateOutage(
        DateTimeOffset disconnectedAtUtc,
        string? mostRecentDisconnectReason)
        => new()
        {
            DisconnectedAtUtc = disconnectedAtUtc.ToUniversalTime(),
            MostRecentDisconnectReason = NormalizeReason(mostRecentDisconnectReason)
        };

    private static string NormalizeReason(string? disconnectReason)
        => string.IsNullOrWhiteSpace(disconnectReason)
            ? "Discord gateway disconnected."
            : disconnectReason;

    public void Dispose() => _fileAccess.Dispose();
}
