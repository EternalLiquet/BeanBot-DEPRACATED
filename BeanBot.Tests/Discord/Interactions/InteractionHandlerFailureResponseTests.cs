using System.Net;
using BeanBot.Discord.Interactions;
using Discord;
using Discord.Net;
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

    [Fact]
    public void IsKnownMissingOriginal_AcceptsOnlyAnUnknownWebhookNotFoundResponse()
    {
        var missingWebhook = CreateHttpException(
            HttpStatusCode.NotFound,
            DiscordErrorCode.UnknownWebhook);
        var deletedMessage = CreateHttpException(
            HttpStatusCode.NotFound,
            DiscordErrorCode.UnknownMessage);
        var unclassifiedNotFound = CreateHttpException(HttpStatusCode.NotFound);

        Assert.True(InteractionResponseErrors.IsKnownMissingOriginal(missingWebhook));
        Assert.False(InteractionResponseErrors.IsKnownMissingOriginal(deletedMessage));
        Assert.False(InteractionResponseErrors.IsKnownMissingOriginal(unclassifiedNotFound));
        Assert.False(InteractionResponseErrors.IsKnownMissingOriginal(
            new InvalidOperationException("not a Discord response")));
    }

    [Fact]
    public void IsCancellationException_RecognizesDirectAndAggregatedCancellation()
    {
        var cancellation = new OperationCanceledException("stopping");

        Assert.True(InteractionHandler.IsCancellationException(cancellation));
        Assert.True(InteractionHandler.IsCancellationException(
            new AggregateException(
                new InvalidOperationException("acknowledgement failed"),
                cancellation)));
        Assert.False(InteractionHandler.IsCancellationException(
            new AggregateException(new InvalidOperationException("network failed"))));
        Assert.False(InteractionHandler.IsCancellationException(
            new InvalidOperationException("network failed")));
    }

    private static HttpException CreateHttpException(
        HttpStatusCode statusCode,
        DiscordErrorCode? discordCode = null)
        => new(statusCode, null, discordCode, "Test Discord failure", null);
}
