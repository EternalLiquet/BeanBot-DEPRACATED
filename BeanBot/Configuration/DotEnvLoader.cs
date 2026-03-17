namespace BeanBot.Configuration;

public static class DotEnvLoader
{
    private static readonly object Sync = new();
    private static bool _loaded;

    public static void LoadIfPresent()
    {
        lock (Sync)
        {
            if (_loaded)
            {
                return;
            }

            string[] candidatePaths =
            [
                Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                Path.Combine(AppContext.BaseDirectory, ".env"),
            ];

            foreach (var candidatePath in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(candidatePath))
                {
                    continue;
                }

                foreach (var rawLine in File.ReadAllLines(candidatePath))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    {
                        continue;
                    }

                    if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                    {
                        line = line["export ".Length..].Trim();
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = line[..separatorIndex].Trim();
                    if (string.IsNullOrWhiteSpace(key) || Environment.GetEnvironmentVariable(key) is not null)
                    {
                        continue;
                    }

                    var value = line[(separatorIndex + 1)..].Trim();
                    Environment.SetEnvironmentVariable(key, TrimMatchingQuotes(value));
                }

                break;
            }

            _loaded = true;
        }
    }

    private static string TrimMatchingQuotes(string value)
    {
        if (value.Length >= 2)
        {
            var hasMatchingDoubleQuotes = value[0] == '"' && value[^1] == '"';
            var hasMatchingSingleQuotes = value[0] == '\'' && value[^1] == '\'';
            if (hasMatchingDoubleQuotes || hasMatchingSingleQuotes)
            {
                return value[1..^1];
            }
        }

        return value;
    }
}
