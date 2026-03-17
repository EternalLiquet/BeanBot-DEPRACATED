namespace BeanBot.Configuration;

public sealed class BeanBotOptions
{
    public string BotToken { get; set; } = string.Empty;

    public string MongoConnectionString { get; set; } = string.Empty;

    public ulong GeneralChannelId { get; set; }

    public string HatoeteUrl { get; set; } = string.Empty;

    public string YoshimaruUrl { get; set; } = string.Empty;

    public ulong? IlServerId { get; set; }

    public string LogLevel { get; set; } = "Information";
}
