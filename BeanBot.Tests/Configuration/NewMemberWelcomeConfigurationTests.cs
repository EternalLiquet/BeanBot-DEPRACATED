using BeanBot.Configuration;
using BeanBot.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeanBot.Tests.Configuration;

public class NewMemberWelcomeConfigurationTests
{
    [Fact]
    public void Options_DefaultsToExistingEnabledWelcomeMessage()
    {
        using var provider = CreateProvider(RequiredSettings());

        var options = provider.GetRequiredService<NewMemberWelcomeOptions>();

        Assert.True(options.Enabled);
        Assert.Equal(NewMemberWelcomeOptions.DefaultMessage, options.Message);
    }

    [Fact]
    public void Options_BindsCanonicalWelcomeSettings()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.NewMemberWelcomeEnabledVariable] = "false";
        values[BeanBotConfiguration.NewMemberWelcomeMessageVariable] = "Custom welcome";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<NewMemberWelcomeOptions>();

        Assert.False(options.Enabled);
        Assert.Equal("Custom welcome", options.Message);
    }

    [Fact]
    public void Options_AcceptsLegacyWelcomeAliases()
    {
        var values = RequiredSettings();
        values["newMemberWelcomeEnabled"] = "true";
        values["newMemberWelcomeMessage"] = "Legacy welcome";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<NewMemberWelcomeOptions>();

        Assert.True(options.Enabled);
        Assert.Equal("Legacy welcome", options.Message);
    }

    [Fact]
    public void Options_DisabledWelcomeAllowsBlankMessage()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.NewMemberWelcomeEnabledVariable] = "false";
        values[BeanBotConfiguration.NewMemberWelcomeMessageVariable] = "";

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<NewMemberWelcomeOptions>();

        Assert.False(options.Enabled);
        Assert.Equal(string.Empty, options.Message);
    }

    [Theory]
    [InlineData("not-a-bool")]
    [InlineData("1")]
    public void Options_RejectsMalformedEnabledValueWithoutEchoingIt(string value)
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.NewMemberWelcomeEnabledVariable] = value;

        using var provider = CreateProvider(values);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<NewMemberWelcomeOptions>());

        Assert.Contains(BeanBotConfiguration.NewMemberWelcomeEnabledVariable, exception.Message);
        Assert.DoesNotContain(value, exception.Message);
    }

    [Fact]
    public void Options_RejectsBlankMessageWhileEnabled()
    {
        var values = RequiredSettings();
        values[BeanBotConfiguration.NewMemberWelcomeMessageVariable] = "   ";

        using var provider = CreateProvider(values);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<NewMemberWelcomeOptions>());

        Assert.Contains(BeanBotConfiguration.NewMemberWelcomeMessageVariable, exception.Message);
    }

    [Fact]
    public void Options_RejectsOversizedMessageWithoutEchoingIt()
    {
        var oversized = new string('x', NewMemberWelcomeOptions.DiscordMessageMaximumLength + 1);
        var values = RequiredSettings();
        values[BeanBotConfiguration.NewMemberWelcomeMessageVariable] = oversized;

        using var provider = CreateProvider(values);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<NewMemberWelcomeOptions>());

        Assert.Contains(BeanBotConfiguration.NewMemberWelcomeMessageVariable, exception.Message);
        Assert.DoesNotContain(oversized, exception.ToString());
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
