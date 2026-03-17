using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BeanBot.Configuration;

public sealed class BeanBotOptionsSetup(IConfiguration configuration) : IConfigureOptions<BeanBotOptions>
{
    public void Configure(BeanBotOptions options)
    {
        options.BotToken = GetValue("BEANBOT_BOT_TOKEN", "botToken");
        options.MongoConnectionString = GetValue("BEANBOT_MONGO_CONNECTION_STRING", "mongoConnectionString");
        options.GeneralChannelId = GetUnsignedLongValue("BEANBOT_GENERAL_CHANNEL_ID", "generalChannelId");
        options.HatoeteUrl = GetValue("BEANBOT_HATOETE_URL", "hatoeteUrl");
        options.YoshimaruUrl = GetValue("BEANBOT_YOSHIMARU_URL", "yoshimaruUrl");
        options.IlServerId = TryGetUnsignedLongValue("BEANBOT_IL_SERVER_ID", "ilServerId");
        options.LogLevel = GetValue("BEANBOT_LOG_LEVEL", "logLevel") ?? "Information";
    }

    private string GetValue(string primaryKey, string legacyKey)
    {
        return configuration[primaryKey]
            ?? configuration[legacyKey]
            ?? string.Empty;
    }

    private ulong GetUnsignedLongValue(string primaryKey, string legacyKey)
    {
        var rawValue = GetValue(primaryKey, legacyKey);
        return ulong.TryParse(rawValue, out var parsedValue)
            ? parsedValue
            : 0;
    }

    private ulong? TryGetUnsignedLongValue(string primaryKey, string legacyKey)
    {
        var rawValue = GetValue(primaryKey, legacyKey);
        return ulong.TryParse(rawValue, out var parsedValue)
            ? parsedValue
            : null;
    }
}
