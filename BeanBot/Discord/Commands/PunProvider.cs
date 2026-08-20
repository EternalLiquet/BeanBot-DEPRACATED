using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using BeanBot.Logging;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Commands;

public sealed class PunProvider : IPunProvider
{
    private readonly string[] _puns;

    public PunProvider(ILogger<PunProvider> logger)
        : this(Path.Combine(AppContext.BaseDirectory, "Resources", "puns.csv"), logger)
    {
    }

    internal PunProvider(string resourcePath, ILogger<PunProvider> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        ArgumentNullException.ThrowIfNull(logger);

        _puns = LoadPuns(resourcePath, logger);
    }

    public bool TryGetRandomPun([NotNullWhen(true)] out string? pun)
    {
        if (_puns.Length == 0)
        {
            pun = null;
            return false;
        }

        pun = _puns[RandomNumberGenerator.GetInt32(_puns.Length)];
        return true;
    }

    private static string[] LoadPuns(string resourcePath, ILogger logger)
    {
        try
        {
            using var reader = new StreamReader(resourcePath);
            var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                DetectColumnCountChanges = true,
                LineBreakInQuotedFieldIsBadData = true
            };
            using var csv = new CsvReader(reader, configuration);
            if (!csv.Read())
            {
                BeanBotLog.PunResourceEmpty(logger, resourcePath);
                return [];
            }

            csv.ReadHeader();
            var puns = csv.GetRecords<Pun>()
                .Select(record => record.BadPost)
                .Where(pun => !string.IsNullOrWhiteSpace(pun))
                .ToArray();

            if (puns.Length == 0)
            {
                BeanBotLog.PunResourceEmpty(logger, resourcePath);
                return [];
            }

            BeanBotLog.PunResourceLoaded(logger, puns.Length, resourcePath);
            return puns;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            BeanBotLog.PunResourceMissing(logger, resourcePath);
            return [];
        }
        catch (Exception exception)
        {
            BeanBotLog.PunResourceInvalid(logger, resourcePath, exception);
            return [];
        }
    }
}
