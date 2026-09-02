using Microsoft.Extensions.Configuration;
using Serilog;

namespace BeanBot.Configuration;

internal static class BeanBotConfiguration
{
    internal const string BotTokenVariable = "BEANBOT_BOT_TOKEN";
    internal const string MongoConnectionVariable = "BEANBOT_MONGO_CONNECTION_STRING";
    internal const string GeneralChannelVariable = "BEANBOT_GENERAL_CHANNEL_ID";
    internal const string HatoeteUrlVariable = "BEANBOT_HATOETE_URL";
    internal const string YoshimaruUrlVariable = "BEANBOT_YOSHIMARU_URL";
    internal const string HealthCheckPortVariable = "BEANBOT_HEALTHCHECK_PORT";
    internal const string HealthCheckBindAddressVariable = "BEANBOT_HEALTHCHECK_BIND_ADDRESS";
    internal const string HealthCheckBearerTokenVariable = "BEANBOT_HEALTHCHECK_BEARER_TOKEN";
    internal const string HealthCheckRateLimitVariable = "BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS";
    internal const string NewMemberWelcomeEnabledVariable = "BEANBOT_NEW_MEMBER_WELCOME_ENABLED";
    internal const string NewMemberWelcomeMessageVariable = "BEANBOT_NEW_MEMBER_WELCOME_MESSAGE";

    private static readonly ConfigurationKey[] RequiredKeys =
    [
        new(BotTokenVariable, "botToken", "BotToken"),
        new(MongoConnectionVariable, "mongoConnectionString", "MongoConnectionString"),
        new(GeneralChannelVariable, "generalChannelId", "GeneralChannelId"),
        new(HatoeteUrlVariable, "hatoeteUrl", "HatoeteUrl"),
        new(YoshimaruUrlVariable, "yoshimaruUrl", "YoshimaruUrl")
    ];

    private static readonly ConfigurationKey HealthCheckPort =
        new(HealthCheckPortVariable, "healthCheckPort", "HealthCheck:Port");

    private static readonly ConfigurationKey[] HealthCheckKeys =
    [
        new(HealthCheckBindAddressVariable, "healthCheckBindAddress", "HealthCheck:BindAddress"),
        new(HealthCheckBearerTokenVariable, "healthCheckBearerToken", "HealthCheck:BearerToken"),
        new(HealthCheckRateLimitVariable, "healthCheckRateLimitSeconds", "HealthCheck:RateLimitSeconds")
    ];

    private static readonly ConfigurationKey[] NewMemberWelcomeKeys =
    [
        new(NewMemberWelcomeEnabledVariable, "newMemberWelcomeEnabled", "NewMemberWelcome:Enabled"),
        new(NewMemberWelcomeMessageVariable, "newMemberWelcomeMessage", "NewMemberWelcome:Message")
    ];

    internal static ConfigurationManager AddBeanBotConfiguration(
        this ConfigurationManager configuration,
        IEnumerable<string>? dotEnvCandidatePaths = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        AddDotEnvDefaults(configuration, dotEnvCandidatePaths ?? DefaultDotEnvCandidatePaths());

        var normalizedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in RequiredKeys)
        {
            AddNormalizedValue(configuration, normalizedValues, key);
        }

        var healthCheckPort = GetCompatibilityValue(configuration, HealthCheckPort);
        if (healthCheckPort is not null)
        {
            normalizedValues[SectionKey(HealthCheckPort.OptionPath)] = healthCheckPort;
        }

        if (!string.IsNullOrWhiteSpace(healthCheckPort))
        {
            foreach (var key in HealthCheckKeys)
            {
                AddNormalizedValue(configuration, normalizedValues, key);
            }
        }

        foreach (var key in NewMemberWelcomeKeys)
        {
            AddNormalizedValue(configuration, normalizedValues, key);
        }

        configuration.AddInMemoryCollection(normalizedValues);
        return configuration;
    }

    private static void AddDotEnvDefaults(
        ConfigurationManager configuration,
        IEnumerable<string> candidatePaths)
    {
        foreach (var candidatePath in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            var defaults = ParseDotEnv(File.ReadLines(candidatePath))
                .Where(pair => configuration[pair.Key] is null)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            configuration.AddInMemoryCollection(defaults!);
            Log.Information("Loaded configuration defaults from {DotEnvPath}", candidatePath);
            return;
        }
    }

    private static Dictionary<string, string> ParseDotEnv(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring("export ".Length).Trim();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separatorIndex).Trim();
            var value = line.Substring(separatorIndex + 1).Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                values.TryAdd(key, TrimMatchingQuotes(value));
            }
        }

        return values;
    }

    private static void AddNormalizedValue(
        IConfiguration configuration,
        Dictionary<string, string?> normalizedValues,
        ConfigurationKey key)
    {
        var value = GetCompatibilityValue(configuration, key);
        if (value is not null)
        {
            normalizedValues[SectionKey(key.OptionPath)] = value;
        }
    }

    private static string? GetCompatibilityValue(IConfiguration configuration, ConfigurationKey key)
        => configuration[key.CanonicalName] ?? configuration[key.LegacyName];

    private static IEnumerable<string> DefaultDotEnvCandidatePaths()
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), ".env");
        yield return Path.Combine(AppContext.BaseDirectory, ".env");
    }

    private static string SectionKey(string optionPath)
        => $"{BeanBotSettings.SectionName}:{optionPath}";

    private static string TrimMatchingQuotes(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        var hasMatchingDoubleQuotes = value[0] == '"' && value[^1] == '"';
        var hasMatchingSingleQuotes = value[0] == '\'' && value[^1] == '\'';
        return hasMatchingDoubleQuotes || hasMatchingSingleQuotes
            ? value.Substring(1, value.Length - 2)
            : value;
    }

    private sealed record ConfigurationKey(
        string CanonicalName,
        string LegacyName,
        string OptionPath);
}
