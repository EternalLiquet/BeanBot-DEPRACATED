using BeanBot.Discord.Interactions;
using Discord;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class InteractionHandlerFailureResponseTests
{
    [Fact]
    public void SetFailureResponse_ReplacesOriginalContentAndClearsInteractiveState()
    {
        var properties = new MessageProperties();

        InteractionHandler.SetFailureResponse(properties);

        Assert.Equal(
            "Bean Bot couldn't complete that command. Try again in a moment.",
            properties.Content.Value);
        Assert.Empty(properties.Embeds.Value);
        Assert.Empty(properties.Components.Value.Components);
        Assert.Same(AllowedMentions.None, properties.AllowedMentions.Value);
    }
}
