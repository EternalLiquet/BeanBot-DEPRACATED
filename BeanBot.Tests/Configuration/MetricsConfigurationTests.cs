using BeanBot.Configuration;
using BeanBot.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeanBot.Tests.Configuration;

public class MetricsConfigurationTests
{
    [Fact]
    public void Metrics_AreDisabledByDefaultWhenHealthListenerIsEnabled()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.HealthCheckPortVariable] = "8080";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.True(options.HealthCheck.Enabled);
        Assert.False(options.HealthCheck.MetricsEnabled);
    }

    [Fact]
    public void Metrics_CanBeEnabledOnExistingHealthListener()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.HealthCheckPortVariable] = "8080";
        values[BeanBotConfiguration.MetricsEnabledVariable] = "true";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.True(options.HealthCheck.Enabled);
        Assert.True(options.HealthCheck.MetricsEnabled);
    }

    [Fact]
    public void Metrics_EnabledWithoutHealthListenerIsRejected()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.MetricsEnabledVariable] = "true";

        using var provider = CreateProvider(values);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<BeanBotOptions>());

        Assert.Contains(BeanBotConfiguration.MetricsEnabledVariable, exception.Message);
        Assert.Contains(BeanBotConfiguration.HealthCheckPortVariable, exception.Message);
    }

    [Fact]
    public void Metrics_InvalidBooleanIsRejectedWithoutEchoingValue()
    {
        const string invalidValue = "definitely-not-a-boolean-secret-marker";
        var values = RequiredSettings();
        values[BeanBotConfiguration.HealthCheckPortVariable] = "8080";
        values[BeanBotConfiguration.MetricsEnabledVariable] = invalidValue;

        using var provider = CreateProvider(values);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<BeanBotOptions>());

        Assert.Contains(BeanBotConfiguration.MetricsEnabledVariable, exception.Message);
        Assert.DoesNotContain(invalidValue, exception.ToString());
    }

    [Fact]
    public void Metrics_LegacyAliasRemainsCompatible()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.HealthCheckPortVariable] = "8080";
        values["metricsEnabled"] = "true";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.True(options.HealthCheck.MetricsEnabled);
    }

    private static ServiceProvider CreateProvider(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        configuration.AddBeanBotConfiguration(Array.Empty<string>());
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
