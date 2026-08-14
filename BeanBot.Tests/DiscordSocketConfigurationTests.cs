using BeanBot.Hosting;

using Discord;

using Xunit;

namespace BeanBot.Tests;

public class DiscordSocketConfigurationTests
{
    [Fact]
    public void Configuration_RequestsOnlyRequiredGatewayIntents()
    {
        var configuration = DiscordSocketConfiguration.Create();
        var expected = GatewayIntents.Guilds
            | GatewayIntents.GuildMembers
            | GatewayIntents.GuildEmojis
            | GatewayIntents.GuildMessages
            | GatewayIntents.DirectMessages
            | GatewayIntents.GuildMessageReactions
            | GatewayIntents.DirectMessageReactions
            | GatewayIntents.MessageContent;

        Assert.Equal(expected, configuration.GatewayIntents);
        Assert.False(configuration.GatewayIntents.HasFlag(GatewayIntents.GuildPresences));
        Assert.Equal(50, configuration.MessageCacheSize);
        Assert.False(configuration.AlwaysDownloadUsers);
        Assert.False(configuration.IncludeRawPayloadOnGatewayErrors);
    }
}
