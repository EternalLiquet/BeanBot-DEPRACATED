using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;

namespace BeanBot.Configuration
{
    internal static class BeanBotOptionsLoader
    {
        private const string BotTokenVariable = "BEANBOT_BOT_TOKEN";
        private const string MongoConnectionVariable = "BEANBOT_MONGO_CONNECTION_STRING";
        private const string GeneralChannelVariable = "BEANBOT_GENERAL_CHANNEL_ID";
        private const string HatoeteUrlVariable = "BEANBOT_HATOETE_URL";
        private const string YoshimaruUrlVariable = "BEANBOT_YOSHIMARU_URL";
        private const string HealthCheckPortVariable = "BEANBOT_HEALTHCHECK_PORT";
        private const string HealthCheckBindAddressVariable = "BEANBOT_HEALTHCHECK_BIND_ADDRESS";
        private const string HealthCheckBearerTokenVariable = "BEANBOT_HEALTHCHECK_BEARER_TOKEN";
        private const string HealthCheckRateLimitVariable = "BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS";

        public static BeanBotOptions LoadFromEnvironment()
        {
            LoadDotEnvFileIfPresent();
            var options = Load(Environment.GetEnvironmentVariable);
            Log.Information("Loaded and validated BeanBot configuration from the environment");
            return options;
        }

        internal static BeanBotOptions Load(Func<string, string> getEnvironmentValue)
        {
            var missingSettings = new List<string>();
            var botToken = GetRequired(getEnvironmentValue, BotTokenVariable, "botToken", missingSettings);
            var mongoConnection = GetRequired(getEnvironmentValue, MongoConnectionVariable, "mongoConnectionString", missingSettings);
            var generalChannel = GetRequired(getEnvironmentValue, GeneralChannelVariable, "generalChannelId", missingSettings);
            var hatoeteUrl = GetRequired(getEnvironmentValue, HatoeteUrlVariable, "hatoeteUrl", missingSettings);
            var yoshimaruUrl = GetRequired(getEnvironmentValue, YoshimaruUrlVariable, "yoshimaruUrl", missingSettings);

            if (missingSettings.Count > 0)
            {
                throw new InvalidOperationException($"Missing required environment variables: {string.Join(", ", missingSettings)}");
            }

            if (!ulong.TryParse(generalChannel, NumberStyles.None, CultureInfo.InvariantCulture, out var generalChannelId))
            {
                throw InvalidValue(GeneralChannelVariable, generalChannel, "a Discord snowflake ID");
            }

            return new BeanBotOptions(
                botToken,
                mongoConnection,
                generalChannelId,
                ParseHttpUri(HatoeteUrlVariable, hatoeteUrl),
                ParseHttpUri(YoshimaruUrlVariable, yoshimaruUrl),
                LoadHealthCheckOptions(getEnvironmentValue));
        }

        private static HealthCheckOptions LoadHealthCheckOptions(Func<string, string> getEnvironmentValue)
        {
            var portValue = GetOptional(getEnvironmentValue, HealthCheckPortVariable, "healthCheckPort");
            if (string.IsNullOrWhiteSpace(portValue))
            {
                return HealthCheckOptions.Disabled;
            }

            if (!int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port < IPEndPoint.MinPort ||
                port > IPEndPoint.MaxPort)
            {
                throw InvalidValue(HealthCheckPortVariable, portValue, "a TCP port from 1 through 65535");
            }

            var bindAddress = IPAddress.Any;
            var bindAddressValue = GetOptional(getEnvironmentValue, HealthCheckBindAddressVariable, "healthCheckBindAddress");
            if (!string.IsNullOrWhiteSpace(bindAddressValue) && !IPAddress.TryParse(bindAddressValue, out bindAddress))
            {
                throw InvalidValue(HealthCheckBindAddressVariable, bindAddressValue, "an IP address such as 0.0.0.0 or 127.0.0.1");
            }

            var minimumPollInterval = TimeSpan.FromSeconds(90);
            var rateLimitValue = GetOptional(getEnvironmentValue, HealthCheckRateLimitVariable, "healthCheckRateLimitSeconds");
            if (!string.IsNullOrWhiteSpace(rateLimitValue))
            {
                if (!int.TryParse(rateLimitValue, NumberStyles.None, CultureInfo.InvariantCulture, out var rateLimitSeconds) ||
                    rateLimitSeconds <= 0)
                {
                    throw InvalidValue(HealthCheckRateLimitVariable, rateLimitValue, "a positive number of seconds");
                }

                minimumPollInterval = TimeSpan.FromSeconds(rateLimitSeconds);
            }

            var bearerToken = GetOptional(getEnvironmentValue, HealthCheckBearerTokenVariable, "healthCheckBearerToken");
            return new HealthCheckOptions(true, bindAddress, port, bearerToken, minimumPollInterval);
        }

        private static Uri ParseHttpUri(string variableName, string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw InvalidValue(variableName, value, "an absolute HTTP or HTTPS URL");
            }

            return uri;
        }

        private static string GetRequired(
            Func<string, string> getEnvironmentValue,
            string preferredName,
            string legacyName,
            ICollection<string> missingSettings)
        {
            var value = GetOptional(getEnvironmentValue, preferredName, legacyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            missingSettings.Add(Describe(preferredName, legacyName));
            return null;
        }

        private static string GetOptional(Func<string, string> getEnvironmentValue, string preferredName, string legacyName)
            => getEnvironmentValue(preferredName) ?? getEnvironmentValue(legacyName);

        private static InvalidOperationException InvalidValue(string variableName, string value, string expectation)
            => new InvalidOperationException($"Invalid value for {variableName}: '{value}'. Expected {expectation}.");

        private static string Describe(string preferredName, string legacyName)
            => $"{preferredName} (or legacy {legacyName})";

        private static void LoadDotEnvFileIfPresent()
        {
            var candidatePaths = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                Path.Combine(AppContext.BaseDirectory, ".env")
            }.Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var candidatePath in candidatePaths)
            {
                if (!File.Exists(candidatePath))
                {
                    continue;
                }

                foreach (var rawLine in File.ReadAllLines(candidatePath))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
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
                    if (string.IsNullOrWhiteSpace(key) || Environment.GetEnvironmentVariable(key) != null)
                    {
                        continue;
                    }

                    Environment.SetEnvironmentVariable(key, TrimMatchingQuotes(value));
                }

                Log.Information("Loaded configuration defaults from {DotEnvPath}", candidatePath);
                return;
            }
        }

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
    }
}
