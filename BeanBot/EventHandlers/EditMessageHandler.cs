using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace BeanBot.EventHandlers;

public sealed class EditMessageHandler(GatewayClient client, ILogger<EditMessageHandler> logger) : IMessageUpdateGatewayHandler
{
    public async ValueTask HandleAsync(Message message)
    {
        if (message.Channel is null || !IsEightBallMessage(message.Content))
        {
            return;
        }

        var nearbyMessages = await message.Channel.GetMessagesAroundAsync(message.Id, 4);
        var orderedMessages = nearbyMessages.OrderBy(nearbyMessage => nearbyMessage.CreatedAt).ToList();
        var updatedMessageIndex = orderedMessages.FindIndex(nearbyMessage => nearbyMessage.Id == message.Id);
        if (updatedMessageIndex < 0 || updatedMessageIndex + 1 >= orderedMessages.Count)
        {
            logger.LogDebug("Could not find an adjacent bot reply for edited 8ball message {MessageId}", message.Id);
            return;
        }

        var botReply = orderedMessages[updatedMessageIndex + 1];
        if (botReply.Author is null || client.Cache.User is not { } currentUser || botReply.Author.Id != currentUser.Id)
        {
            return;
        }

        await message.Channel.ModifyMessageAsync(botReply.Id, properties => properties.Content = "Do not edit your 8ball requests in my presence, mortal.");
        logger.LogDebug("Modified adjacent 8ball response for edited message {MessageId}", message.Id);
    }

    private static bool IsEightBallMessage(string content)
    {
        return content.StartsWith("%8ball", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("succ 8ball", StringComparison.OrdinalIgnoreCase);
    }
}
