using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;

namespace BeanBot.Configuration;

internal sealed class BeanBotSettingsValidator : IValidateOptions<BeanBotSettings>
{
    public ValidateOptionsResult Validate(string? name, BeanBotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var failures = new List<string>();
        Require(settings.BotToken, BeanBotConfiguration.BotTokenVariable, "botToken", failures);
        Require(
            settings.MongoConnectionString,
            BeanBotConfiguration.MongoConnectionVariable,
            "mongoConnectionString",
            failures);

        if (string.IsNullOrWhiteSpace(settings.GeneralChannelId))
        {
            failures.Add(Missing(BeanBotConfiguration.GeneralChannelVariable, "generalChannelId"));
        }
        else if (!ulong.TryParse(
            settings.GeneralChannelId,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _))
        {
            failures.Add(
                $"Invalid value for {BeanBotConfiguration.GeneralChannelVariable}. " +
                "Expected a Discord snowflake ID.");
        }

        ValidateHttpUri(
            settings.HatoeteUrl,
            BeanBotConfiguration.HatoeteUrlVariable,
            "hatoeteUrl",
            failures);
        ValidateHttpUri(
            settings.YoshimaruUrl,
            BeanBotConfiguration.YoshimaruUrlVariable,
            "yoshimaruUrl",
            failures);

        ValidateHealthCheck(settings.HealthCheck, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateHealthCheck(
        BeanBotHealthCheckSettings settings,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(settings.Port))
        {
            return;
        }

        if (!int.TryParse(settings.Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port < IPEndPoint.MinPort ||
            port > IPEndPoint.MaxPort)
        {
            failures.Add(
                $"Invalid value for {BeanBotConfiguration.HealthCheckPortVariable}. " +
                "Expected a TCP port from 0 through 65535.");
        }

        if (!string.IsNullOrWhiteSpace(settings.BindAddress) &&
            !IPAddress.TryParse(settings.BindAddress, out _))
        {
            failures.Add(
                $"Invalid value for {BeanBotConfiguration.HealthCheckBindAddressVariable}. " +
                "Expected an IP address such as 0.0.0.0 or 127.0.0.1.");
        }

        if (!string.IsNullOrWhiteSpace(settings.RateLimitSeconds) &&
            (!int.TryParse(
                settings.RateLimitSeconds,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var rateLimitSeconds) ||
             rateLimitSeconds <= 0))
        {
            failures.Add(
                $"Invalid value for {BeanBotConfiguration.HealthCheckRateLimitVariable}. " +
                "Expected a positive number of seconds.");
        }
    }

    private static void ValidateHttpUri(
        string? value,
        string variableName,
        string legacyName,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(Missing(variableName, legacyName));
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add(
                $"Invalid value for {variableName}. Expected an absolute HTTP or HTTPS URL.");
        }
    }

    private static void Require(
        string? value,
        string variableName,
        string legacyName,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(Missing(variableName, legacyName));
        }
    }

    private static string Missing(string variableName, string legacyName)
        => $"Missing required environment variable: {variableName} (or legacy {legacyName}).";
}
