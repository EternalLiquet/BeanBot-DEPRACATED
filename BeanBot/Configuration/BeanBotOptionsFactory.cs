using System.Globalization;
using System.Net;

namespace BeanBot.Configuration;

internal static class BeanBotOptionsFactory
{
    internal static BeanBotOptions Create(BeanBotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new BeanBotOptions(
            settings.BotToken!,
            settings.MongoConnectionString!,
            ulong.Parse(settings.GeneralChannelId!, NumberStyles.None, CultureInfo.InvariantCulture),
            new Uri(settings.HatoeteUrl!, UriKind.Absolute),
            new Uri(settings.YoshimaruUrl!, UriKind.Absolute),
            CreateHealthCheckOptions(settings.HealthCheck));
    }

    private static HealthCheckOptions CreateHealthCheckOptions(BeanBotHealthCheckSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Port))
        {
            return HealthCheckOptions.Disabled;
        }

        var bindAddress = string.IsNullOrWhiteSpace(settings.BindAddress)
            ? IPAddress.Any
            : IPAddress.Parse(settings.BindAddress);
        var port = int.Parse(settings.Port, NumberStyles.None, CultureInfo.InvariantCulture);
        var rateLimitSeconds = string.IsNullOrWhiteSpace(settings.RateLimitSeconds)
            ? 90
            : int.Parse(settings.RateLimitSeconds, NumberStyles.None, CultureInfo.InvariantCulture);
        var minimumPollInterval = TimeSpan.FromSeconds(rateLimitSeconds);

        return new HealthCheckOptions(
            true,
            bindAddress,
            port,
            settings.BearerToken,
            minimumPollInterval);
    }
}
