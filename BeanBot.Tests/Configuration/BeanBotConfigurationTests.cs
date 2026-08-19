using System.Net;
using BeanBot.Configuration;
using BeanBot.Hosting;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeanBot.Tests.Configuration;

public class BeanBotConfigurationTests
{
    [Fact]
    public void Options_ParsesCanonicalSettingsAndHealthDefaults()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.HealthCheckPortVariable] = "8080";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal("token", options.BotToken);
        Assert.Equal("mongodb://localhost:27017", options.MongoConnectionString);
        Assert.Equal((ulong)123, options.GeneralChannelId);
        Assert.Equal(new Uri("https://example.com/hatoete.png"), options.HatoeteImageUrl);
        Assert.Equal(new Uri("https://example.com/yoshimaru.png"), options.YoshimaruImageUrl);
        Assert.True(options.HealthCheck.Enabled);
        Assert.Equal(8080, options.HealthCheck.Port);
        Assert.Equal(IPAddress.Any, options.HealthCheck.BindAddress);
        Assert.Equal(TimeSpan.FromSeconds(90), options.HealthCheck.MinimumPollInterval);
        Assert.Null(options.HealthCheck.BearerToken);
    }

    [Fact]
    public void Options_ReportsAllMissingRequiredSettingsTogether()
    {
        using var provider = CreateProvider(new Dictionary<string, string?>());

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<BeanBotOptions>());

        Assert.Contains(BeanBotConfiguration.BotTokenVariable, exception.Message);
        Assert.Contains(BeanBotConfiguration.MongoConnectionVariable, exception.Message);
        Assert.Contains(BeanBotConfiguration.GeneralChannelVariable, exception.Message);
        Assert.Contains(BeanBotConfiguration.HatoeteUrlVariable, exception.Message);
        Assert.Contains(BeanBotConfiguration.YoshimaruUrlVariable, exception.Message);
    }

    [Theory]
    [InlineData("BEANBOT_GENERAL_CHANNEL_ID", "not-a-snowflake")]
    [InlineData("BEANBOT_GENERAL_CHANNEL_ID", "18446744073709551616")]
    [InlineData("BEANBOT_HATOETE_URL", "relative/image.png")]
    [InlineData("BEANBOT_HATOETE_URL", "file:///tmp/image.png")]
    [InlineData("BEANBOT_HEALTHCHECK_PORT", "not-a-port")]
    [InlineData("BEANBOT_HEALTHCHECK_PORT", "70000")]
    [InlineData("BEANBOT_HEALTHCHECK_BIND_ADDRESS", "localhost")]
    [InlineData("BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS", "not-a-number")]
    [InlineData("BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS", "0")]
    [InlineData("BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS", "-1")]
    public void Options_RejectsMalformedValuesWithoutEchoingThem(string key, string value)
    {
        var values = RequiredSettings();
        values[key] = value;
        if (key is "BEANBOT_HEALTHCHECK_BIND_ADDRESS" or "BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS")
        {
            values[BeanBotConfiguration.HealthCheckPortVariable] = "8080";
        }

        using var provider = CreateProvider(values);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<BeanBotOptions>());

        Assert.Contains(key, exception.Message);
        Assert.DoesNotContain(value, exception.Message);
    }

    [Fact]
    public void Options_DisablesHealthAndIgnoresAuxiliaryValuesWhenPortIsBlank()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.HealthCheckPortVariable] = "  ";
        values[BeanBotConfiguration.HealthCheckBindAddressVariable] = "not-an-address";
        values[BeanBotConfiguration.HealthCheckRateLimitVariable] = "not-a-number";
        values[BeanBotConfiguration.HealthCheckBearerTokenVariable] = "health-secret";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Same(HealthCheckOptions.Disabled, options.HealthCheck);
    }

    [Fact]
    public void Options_AcceptsEnabledHealthSettingsAndPortZeroCompatibility()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.HealthCheckPortVariable] = "0";
        values[BeanBotConfiguration.HealthCheckBindAddressVariable] = "127.0.0.1";
        values[BeanBotConfiguration.HealthCheckRateLimitVariable] = "12";
        values[BeanBotConfiguration.HealthCheckBearerTokenVariable] = "health-secret";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.True(options.HealthCheck.Enabled);
        Assert.Equal(0, options.HealthCheck.Port);
        Assert.Equal(IPAddress.Loopback, options.HealthCheck.BindAddress);
        Assert.Equal(TimeSpan.FromSeconds(12), options.HealthCheck.MinimumPollInterval);
        Assert.Equal("health-secret", options.HealthCheck.BearerToken);
    }

    [Fact]
    public void Options_AcceptsAllLegacyAliases()
    {
        var values = new Dictionary<string, string?>
        {
            ["botToken"] = "legacy-token",
            ["mongoConnectionString"] = "mongodb://legacy:27017",
            ["generalChannelId"] = "456",
            ["hatoeteUrl"] = "https://legacy.example/hatoete.png",
            ["yoshimaruUrl"] = "https://legacy.example/yoshimaru.png",
            ["healthCheckPort"] = "8081",
            ["healthCheckBindAddress"] = "127.0.0.1",
            ["healthCheckBearerToken"] = "legacy-health-token",
            ["healthCheckRateLimitSeconds"] = "15"
        };

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal("legacy-token", options.BotToken);
        Assert.Equal("mongodb://legacy:27017", options.MongoConnectionString);
        Assert.Equal((ulong)456, options.GeneralChannelId);
        Assert.Equal(8081, options.HealthCheck.Port);
        Assert.Equal(IPAddress.Loopback, options.HealthCheck.BindAddress);
        Assert.Equal("legacy-health-token", options.HealthCheck.BearerToken);
        Assert.Equal(TimeSpan.FromSeconds(15), options.HealthCheck.MinimumPollInterval);
    }

    [Fact]
    public void Options_CanonicalValueWinsLegacyIncludingWhenCanonicalIsBlank()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.BotTokenVariable] = " ";
        values["botToken"] = "legacy-token";

        using var provider = CreateProvider(values);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<BeanBotOptions>());

        Assert.Contains(BeanBotConfiguration.BotTokenVariable, exception.Message);
        Assert.DoesNotContain("legacy-token", exception.Message);
    }

    [Fact]
    public void Options_BindsHierarchicalConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BeanBot:BotToken"] = "section-token",
            ["BeanBot:MongoConnectionString"] = "mongodb://section:27017",
            ["BeanBot:GeneralChannelId"] = "321",
            ["BeanBot:HatoeteUrl"] = "https://section.example/hatoete.png",
            ["BeanBot:YoshimaruUrl"] = "https://section.example/yoshimaru.png"
        });
        configuration.AddBeanBotConfiguration(Array.Empty<string>());

        using var provider = CreateProvider(configuration);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal("section-token", options.BotToken);
        Assert.Equal((ulong)321, options.GeneralChannelId);
    }

    [Fact]
    public async Task ValidateOnStart_RejectsInvalidConfigurationBeforeApplicationStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddBeanBotConfiguration(Array.Empty<string>());
        builder.Services.AddBeanBot(builder.Configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(BeanBotConfiguration.BotTokenVariable, exception.Message);
        host.Services.GetRequiredService<DiscordSocketClient>().Dispose();
    }

    [Fact]
    public void DotEnv_UsesFirstCandidateParsesSupportedSyntaxAndDoesNotOverrideConfiguration()
    {
        var firstDirectory = Directory.CreateTempSubdirectory("beanbot-dotenv-first-");
        var secondDirectory = Directory.CreateTempSubdirectory("beanbot-dotenv-second-");
        try
        {
            var firstPath = Path.Combine(firstDirectory.FullName, ".env");
            var secondPath = Path.Combine(secondDirectory.FullName, ".env");
            var processMarkerKey = $"BEANBOT_DOTENV_TEST_{Guid.NewGuid():N}";
            File.WriteAllLines(firstPath,
            [
                "# ignored",
                "malformed",
                "export BEANBOT_BOT_TOKEN='dotenv-token'",
                "BEANBOT_BOT_TOKEN=duplicate-token",
                "BEANBOT_MONGO_CONNECTION_STRING=\"mongodb://dotenv:27017\"",
                "BEANBOT_GENERAL_CHANNEL_ID=789",
                "BEANBOT_HATOETE_URL=https://dotenv.example/hatoete.png",
                "BEANBOT_YOSHIMARU_URL=https://dotenv.example/yoshimaru.png",
                $"{processMarkerKey}=must-not-escape"
            ]);
            File.WriteAllText(secondPath, "BEANBOT_BOT_TOKEN=second-file-token");

            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BeanBotConfiguration.BotTokenVariable] = "environment-token"
            });
            configuration.AddBeanBotConfiguration([firstPath, secondPath]);

            using var provider = CreateProvider(configuration);
            var options = provider.GetRequiredService<BeanBotOptions>();

            Assert.Equal("environment-token", options.BotToken);
            Assert.Equal("mongodb://dotenv:27017", options.MongoConnectionString);
            Assert.Equal((ulong)789, options.GeneralChannelId);
            Assert.Null(Environment.GetEnvironmentVariable(processMarkerKey));
        }
        finally
        {
            firstDirectory.Delete(true);
            secondDirectory.Delete(true);
        }
    }

    [Fact]
    public void Options_ValidationNeverIncludesSecretValues()
    {
        const string botSecret = "unique-bot-secret-marker";
        const string mongoSecret = "unique-mongo-secret-marker";
        const string healthSecret = "unique-health-secret-marker";
        var values = RequiredSettings();
        values[BeanBotConfiguration.BotTokenVariable] = botSecret;
        values[BeanBotConfiguration.MongoConnectionVariable] = mongoSecret;
        values[BeanBotConfiguration.HealthCheckPortVariable] = "8080";
        values[BeanBotConfiguration.HealthCheckBearerTokenVariable] = healthSecret;
        values[BeanBotConfiguration.HatoeteUrlVariable] = "invalid";

        using var provider = CreateProvider(values);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<BeanBotOptions>());

        Assert.DoesNotContain(botSecret, exception.ToString());
        Assert.DoesNotContain(mongoSecret, exception.ToString());
        Assert.DoesNotContain(healthSecret, exception.ToString());
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
