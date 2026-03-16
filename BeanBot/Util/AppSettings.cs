using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeanBot.Util
{
    public static class AppSettings
    {
        private static readonly SettingDefinition[] SettingDefinitions =
        {
            new SettingDefinition("botToken", "BEANBOT_BOT_TOKEN", true),
            new SettingDefinition("mongoConnectionString", "BEANBOT_MONGO_CONNECTION_STRING", true),
            new SettingDefinition("generalChannelId", "BEANBOT_GENERAL_CHANNEL_ID", true),
            new SettingDefinition("hatoeteUrl", "BEANBOT_HATOETE_URL", true),
            new SettingDefinition("yoshimaruUrl", "BEANBOT_YOSHIMARU_URL", true),
            new SettingDefinition("ilServerId", "BEANBOT_IL_SERVER_ID", false)
        };

        public static Dictionary<string, string> Settings { get; private set; }

        public static void LoadFromEnvironment()
        {
            LoadDotEnvFileIfPresent();

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missingVariables = new List<string>();

            foreach (var definition in SettingDefinitions)
            {
                var value = GetEnvironmentValue(definition);
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (definition.Required)
                    {
                        missingVariables.Add(definition.ToDisplayString());
                    }

                    continue;
                }

                settings[definition.LegacyKey] = value;
            }

            if (missingVariables.Count > 0)
            {
                var message = $"Missing required environment variables: {string.Join(", ", missingVariables)}";
                Log.Fatal(message);
                throw new InvalidOperationException(message);
            }

            Settings = settings;
            Log.Information("Loaded {SettingCount} application settings from environment variables", Settings.Count);
        }

        public static string DescribeSetting(string legacyKey)
        {
            var definition = SettingDefinitions.FirstOrDefault(setting => setting.LegacyKey.Equals(legacyKey, StringComparison.Ordinal));
            return definition?.ToDisplayString() ?? legacyKey;
        }

        private static string GetEnvironmentValue(SettingDefinition definition)
        {
            return Environment.GetEnvironmentVariable(definition.EnvironmentVariableName)
                ?? Environment.GetEnvironmentVariable(definition.LegacyKey);
        }

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
                    var existingEnvironmentValue = string.IsNullOrWhiteSpace(key)
                        ? null
                        : Environment.GetEnvironmentVariable(key);

                    if (string.IsNullOrWhiteSpace(key) || existingEnvironmentValue is not null)
                    {
                        continue;
                    }

                    Environment.SetEnvironmentVariable(key, TrimMatchingQuotes(value));
                }

                Log.Information("Loaded configuration defaults from .env file at {DotEnvPath}", candidatePath);
                return;
            }
        }

        private static string TrimMatchingQuotes(string value)
        {
            if (value.Length >= 2)
            {
                var startsWithDoubleQuote = value[0] == '"' && value[^1] == '"';
                var startsWithSingleQuote = value[0] == '\'' && value[^1] == '\'';
                if (startsWithDoubleQuote || startsWithSingleQuote)
                {
                    return value.Substring(1, value.Length - 2);
                }
            }

            return value;
        }

        private sealed class SettingDefinition
        {
            public SettingDefinition(string legacyKey, string environmentVariableName, bool required)
            {
                LegacyKey = legacyKey;
                EnvironmentVariableName = environmentVariableName;
                Required = required;
            }

            public string LegacyKey { get; }
            public string EnvironmentVariableName { get; }
            public bool Required { get; }

            public string ToDisplayString()
                => $"{EnvironmentVariableName} (or legacy {LegacyKey})";
        }
    }
}
