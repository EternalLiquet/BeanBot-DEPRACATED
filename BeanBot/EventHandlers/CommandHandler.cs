using BeanBot.Services;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Services;
using NetCord.Services.Commands;

namespace BeanBot.EventHandlers;

public sealed class CommandHandler(
    GatewayClient client,
    CommandService<CommandContext> commandService,
    IServiceProvider serviceProvider,
    MessagePromptService messagePromptService,
    EightBallQueueService eightBallQueueService,
    ILogger<CommandHandler> logger) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message message)
    {
        messagePromptService.PublishMessage(message);

        if (message.Author is null || message.Author.IsBot)
        {
            return;
        }

        HandleQueuedEightBallOverride(message);

        var prefixLength = GetPrefixLength(message.Content);
        if (prefixLength is null)
        {
            return;
        }

        var context = new CommandContext(message, client);
        var result = await commandService.ExecuteAsync(prefixLength.Value, context, serviceProvider);
        await HandleCommandResultAsync(message, result);
    }

    private void HandleQueuedEightBallOverride(Message message)
    {
        if (message.Author.Id != 114559039731531781 ||
            !message.Content.Contains("queue8", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var queuedAnswer = message.Content.Contains("yes", StringComparison.OrdinalIgnoreCase)
            ? "positive"
            : "negative";

        eightBallQueueService.Queue(queuedAnswer, message.Author.Id);
        logger.LogDebug("Queued 8ball override {QueuedAnswer} for user {UserId}", queuedAnswer, message.Author.Id);
    }

    private int? GetPrefixLength(string content)
    {
        if (content.StartsWith('%'))
        {
            return 1;
        }

        const string succPrefix = "succ ";
        if (content.StartsWith(succPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return succPrefix.Length;
        }

        var mentionPrefixes = new[]
        {
            $"<@{client.Id}>",
            $"<@!{client.Id}>",
        };

        foreach (var mentionPrefix in mentionPrefixes)
        {
            if (!content.StartsWith(mentionPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var prefixLength = mentionPrefix.Length;
            if (content.Length > prefixLength && content[prefixLength] == ' ')
            {
                prefixLength++;
            }

            return prefixLength;
        }

        return null;
    }

    private async ValueTask HandleCommandResultAsync(Message message, IExecutionResult result)
    {
        var resultTypeName = result.GetType().Name;
        if (resultTypeName == "SuccessResult" || resultTypeName == "NotFoundResult")
        {
            logger.LogDebug("Command execution for message {MessageId} completed with result {ResultType}", message.Id, resultTypeName);
            return;
        }

        var resultMessage = result.GetType().GetProperty("Message")?.GetValue(result) as string;
        var exception = result.GetType().GetProperty("Exception")?.GetValue(result) as Exception;

        if (exception is not null)
        {
            logger.LogError(exception, "Command execution for message {MessageId} failed with result {ResultType}: {ResultMessage}", message.Id, resultTypeName, resultMessage);
        }
        else
        {
            logger.LogWarning("Command execution for message {MessageId} failed with result {ResultType}: {ResultMessage}", message.Id, resultTypeName, resultMessage);
        }

        if (!string.IsNullOrWhiteSpace(resultMessage) && message.Channel is not null)
        {
            await message.Channel.SendMessageAsync(new NetCord.Rest.MessageProperties
            {
                Content = resultMessage,
            });
        }
    }
}
