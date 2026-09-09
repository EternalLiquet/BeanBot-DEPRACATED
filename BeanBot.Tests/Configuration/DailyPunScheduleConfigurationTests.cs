using BeanBot.Configuration;
using BeanBot.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeanBot.Tests.Configuration;

public class DailyPunScheduleConfigurationTests
{
    [Fact]
    public void Options_OmittedDailyPunSettingsPreserveExistingSchedule()
    {
        using var provider = CreateProvider(RequiredSettings());
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal(new TimeSpan(16, 20, 0), options.DailyPun.LocalTime);
        Assert.Equal("America/Chicago", options.DailyPun.TimeZoneId);
        Assert.NotNull(options.DailyPun.TimeZone);
    }

    [Fact]
    public void Options_ParsesCanonicalDailyPunSchedule()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.DailyPunTimeVariable] = "09:05";
        values[BeanBotConfiguration.DailyPunTimeZoneVariable] = "Europe/London";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal(new TimeSpan(9, 5, 0), options.DailyPun.LocalTime);
        Assert.Equal("Europe/London", options.DailyPun.TimeZoneId);
    }

    [Theory]
    [InlineData("BEANBOT_DAILY_PUN_TIME", "9:05")]
    [InlineData("BEANBOT_DAILY_PUN_TIME", "24:00")]
    [InlineData("BEANBOT_DAILY_PUN_TIMEZONE", "Mars/Olympus")]
    [InlineData("BEANBOT_DAILY_PUN_TIMEZONE", " ")]
    public void Options_RejectsMalformedDailyPunScheduleWithoutEchoingValue(string key, string value)
    {
        var values = RequiredSettings();
        values[key] = value;

        using var provider = CreateProvider(values);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<BeanBotOptions>());

        Assert.Contains(key, exception.Message);
        Assert.DoesNotContain(value, exception.Message);
    }

    [Theory]
    [InlineData("America/Chicago")]
    [InlineData("Central Standard Time")]
    public void Options_ResolvesIanaAndWindowsTimeZoneIds(string timeZoneId)
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.DailyPunTimeZoneVariable] = timeZoneId;

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal(timeZoneId, options.DailyPun.TimeZoneId);
        Assert.NotNull(options.DailyPun.TimeZone);
    }

    [Fact]
    public void Options_BindsHierarchicalDailyPunSchedule()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BeanBot:BotToken"] = "section-token",
            ["BeanBot:MongoConnectionString"] = "mongodb://section:27017",
            ["BeanBot:GeneralChannelId"] = "321",
            ["BeanBot:HatoeteUrl"] = "https://section.example/hatoete.png",
            ["BeanBot:YoshimaruUrl"] = "https://section.example/yoshimaru.png",
            ["BeanBot:DailyPun:Time"] = "07:45",
            ["BeanBot:DailyPun:TimeZone"] = "UTC"
        });
        configuration.AddBeanBotConfiguration(Array.Empty<string>());

        using var provider = CreateProvider(configuration);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal(new TimeSpan(7, 45, 0), options.DailyPun.LocalTime);
        Assert.Equal("UTC", options.DailyPun.TimeZoneId);
    }

    private static ServiceProvider CreateProvider(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        configuration.AddBeanBotConfiguration(Array.Empty<string>());
        return CreateProvider(configuration);
    }

    private static ServiceProvider CreateProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddBeanBot(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> RequiredSettings() => new()
    {
        [BeanBotConfiguration.BotTokenVariable] = "token",
        [BeanBotConfiguration.MongoConnectionVariable] = "mongodb://localhost:27017",
        [BeanBotConfiguration.GeneralChannelVariable] = "123",
        [BeanBotConfiguration.HatoeteUrlVariable] = "https://example.com/hatoete.png",
        [BeanBotConfiguration.YoshimaruUrlVariable] = "https://example.com/yoshimaru.png"
    };
}
