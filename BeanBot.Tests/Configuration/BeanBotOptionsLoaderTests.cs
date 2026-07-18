using BeanBot.Configuration;
using System.Net;
using Xunit;

namespace BeanBot.Tests.Configuration;

public class BeanBotOptionsLoaderTests
{
    [Fact]
    public void Load_ParsesAndValidatesTypedSettings()
    {
        var values = RequiredSettings();
        values["BEANBOT_HEALTHCHECK_PORT"] = "8080";
        values["BEANBOT_HEALTHCHECK_BIND_ADDRESS"] = "127.0.0.1";
        values["BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS"] = "12";

        var options = BeanBotOptionsLoader.Load(name => values.GetValueOrDefault(name));

        Assert.Equal((ulong)123, options.GeneralChannelId);
        Assert.Equal(new Uri("https://example.com/hatoete.png"), options.HatoeteImageUrl);
        Assert.True(options.HealthCheck.Enabled);
        Assert.Equal(8080, options.HealthCheck.Port);
        Assert.Equal(IPAddress.Loopback, options.HealthCheck.BindAddress);
        Assert.Equal(TimeSpan.FromSeconds(12), options.HealthCheck.MinimumPollInterval);
    }

    [Fact]
    public void Load_ReportsAllMissingRequiredSettingsTogether()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BeanBotOptionsLoader.Load(_ => null));

        Assert.Contains("BEANBOT_BOT_TOKEN", exception.Message);
        Assert.Contains("BEANBOT_MONGO_CONNECTION_STRING", exception.Message);
        Assert.Contains("BEANBOT_GENERAL_CHANNEL_ID", exception.Message);
        Assert.Contains("BEANBOT_HATOETE_URL", exception.Message);
        Assert.Contains("BEANBOT_YOSHIMARU_URL", exception.Message);
    }

    [Theory]
    [InlineData("BEANBOT_GENERAL_CHANNEL_ID", "not-a-snowflake")]
    [InlineData("BEANBOT_HATOETE_URL", "file:///tmp/image.png")]
    [InlineData("BEANBOT_HEALTHCHECK_PORT", "70000")]
    [InlineData("BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS", "0")]
    public void Load_RejectsInvalidValues(string key, string value)
    {
        var values = RequiredSettings();
        values[key] = value;
        if (key == "BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS")
        {
            values["BEANBOT_HEALTHCHECK_PORT"] = "8080";
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BeanBotOptionsLoader.Load(name => values.GetValueOrDefault(name)));

        Assert.Contains(key, exception.Message);
    }

    private static Dictionary<string, string?> RequiredSettings() => new()
    {
        ["BEANBOT_BOT_TOKEN"] = "token",
        ["BEANBOT_MONGO_CONNECTION_STRING"] = "mongodb://localhost:27017",
        ["BEANBOT_GENERAL_CHANNEL_ID"] = "123",
        ["BEANBOT_HATOETE_URL"] = "https://example.com/hatoete.png",
        ["BEANBOT_YOSHIMARU_URL"] = "https://example.com/yoshimaru.png"
    };
}
