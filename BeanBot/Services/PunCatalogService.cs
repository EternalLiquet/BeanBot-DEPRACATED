using BeanBot.Entities;
using BeanBot.Util;
using CsvHelper;
using Microsoft.Extensions.Logging;

namespace BeanBot.Services;

public sealed class PunCatalogService(ILogger<PunCatalogService> logger) : IPunCatalogService
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyList<string>? _cachedPuns;

    public async Task<IReadOnlyList<string>> GetPunsAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedPuns is not null)
        {
            return _cachedPuns;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedPuns is not null)
            {
                return _cachedPuns;
            }

            using var reader = new StreamReader(Path.Combine(DirectorySetup.ResourcesDirectory, "puns.csv"));
            using var csvReader = new CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);

            _cachedPuns = csvReader.GetRecords<Pun>()
                .Select(record => record.BadPost)
                .Where(pun => !string.IsNullOrWhiteSpace(pun))
                .ToList();

            logger.LogDebug("Loaded {PunCount} pun entries from CSV", _cachedPuns.Count);
            return _cachedPuns;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
