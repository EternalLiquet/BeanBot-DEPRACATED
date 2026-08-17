namespace BeanBot.Configuration
{
    internal sealed class BeanBotSettings
    {
        internal const string SectionName = "BeanBot";

        public string? BotToken { get; set; }
        public string? MongoConnectionString { get; set; }
        public string? GeneralChannelId { get; set; }
        public string? HatoeteUrl { get; set; }
        public string? YoshimaruUrl { get; set; }
        public BeanBotHealthCheckSettings HealthCheck { get; set; } = new BeanBotHealthCheckSettings();
    }

    internal sealed class BeanBotHealthCheckSettings
    {
        public string? Port { get; set; }
        public string? BindAddress { get; set; }
        public string? BearerToken { get; set; }
        public string? RateLimitSeconds { get; set; }
    }
}
