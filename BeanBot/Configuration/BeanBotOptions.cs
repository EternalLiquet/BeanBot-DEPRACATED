using System;
using System.Net;

namespace BeanBot.Configuration
{
    public sealed class BeanBotOptions
    {
        public BeanBotOptions(
            string botToken,
            string mongoConnectionString,
            ulong generalChannelId,
            Uri hatoeteImageUrl,
            Uri yoshimaruImageUrl,
            HealthCheckOptions healthCheck)
        {
            BotToken = botToken;
            MongoConnectionString = mongoConnectionString;
            GeneralChannelId = generalChannelId;
            HatoeteImageUrl = hatoeteImageUrl;
            YoshimaruImageUrl = yoshimaruImageUrl;
            HealthCheck = healthCheck;
        }

        public string BotToken { get; }
        public string MongoConnectionString { get; }
        public ulong GeneralChannelId { get; }
        public Uri HatoeteImageUrl { get; }
        public Uri YoshimaruImageUrl { get; }
        public HealthCheckOptions HealthCheck { get; }
    }

    public sealed class HealthCheckOptions
    {
        public HealthCheckOptions(
            bool enabled,
            IPAddress bindAddress,
            int port,
            string bearerToken,
            TimeSpan minimumPollInterval)
        {
            Enabled = enabled;
            BindAddress = bindAddress;
            Port = port;
            BearerToken = bearerToken;
            MinimumPollInterval = minimumPollInterval;
        }

        public bool Enabled { get; }
        public IPAddress BindAddress { get; }
        public int Port { get; }
        public string Path { get; } = "/healthz";
        public string BearerToken { get; }
        public TimeSpan MinimumPollInterval { get; }

        public static HealthCheckOptions Disabled { get; } = new HealthCheckOptions(
            false,
            IPAddress.Loopback,
            0,
            null,
            TimeSpan.FromSeconds(90));
    }
}
