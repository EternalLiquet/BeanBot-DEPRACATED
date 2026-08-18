using BeanBot.Util;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Linq;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    public sealed class EditMessageEventServices
    {
        private const string EditWarning = "Do not edit your 8ball requests in my presence, mortal.";
        private static readonly SearchValues<char> CommandSeparators = SearchValues.Create(" \t\r\n");
        private readonly DiscordSocketClient _discordClient;
        private readonly ILogger<EditMessageEventServices> _logger;

        public EditMessageEventServices(
            DiscordSocketClient discordClient,
            ILogger<EditMessageEventServices> logger)
        {
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task HandleUpdate(
            Cacheable<IMessage, ulong> oldMessage,
            SocketMessage newMessage,
            ISocketMessageChannel messageChannel)
        {
            var previousMessage = await oldMessage.GetOrDownloadAsync();
            if (previousMessage == null || _discordClient.CurrentUser == null ||
                !IsFortuneCommand(previousMessage.Content, _discordClient.CurrentUser.Id))
            {
                return;
            }

            var botResponse = messageChannel.CachedMessages
                .OfType<SocketUserMessage>()
                .Where(message => message.Author.Id == _discordClient.CurrentUser.Id)
                .Where(message => message.Id > newMessage.Id)
                .Where(message => message.Timestamp - newMessage.Timestamp <= TimeSpan.FromMinutes(2))
                .OrderBy(message => message.Id)
                .FirstOrDefault();

            if (botResponse == null)
            {
                BeanBotLog.FortuneResponseMissing(_logger, newMessage.Id);
                return;
            }

            await ReplaceResponseAsync(botResponse);
        }

        internal static bool IsFortuneCommand(string content, ulong botUserId = 0)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var commandText = content.TrimStart();
            if (commandText[0] == '%')
            {
                commandText = commandText.Substring(1).TrimStart();
            }
            else if (commandText.StartsWith("succ ", StringComparison.OrdinalIgnoreCase))
            {
                commandText = commandText.Substring("succ ".Length).TrimStart();
            }
            else if (botUserId != 0 &&
                     (commandText.StartsWith($"<@{botUserId}> ", StringComparison.Ordinal) ||
                      commandText.StartsWith($"<@!{botUserId}> ", StringComparison.Ordinal)))
            {
                var mentionEnd = commandText.IndexOf('>');
                commandText = commandText.Substring(mentionEnd + 1).TrimStart();
            }
            else
            {
                return false;
            }

            var commandEnd = commandText.AsSpan().IndexOfAny(CommandSeparators);
            var command = commandEnd < 0 ? commandText : commandText.Substring(0, commandEnd);
            return string.Equals(command, "8ball", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(command, "fortune", StringComparison.OrdinalIgnoreCase);
        }

        private async Task ReplaceResponseAsync(SocketUserMessage botResponse)
        {
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await botResponse.ModifyAsync(properties => properties.Content = EditWarning);
                    return;
                }
                catch (Exception exception) when (attempt < maxAttempts)
                {
                    BeanBotLog.FortuneResponseReplaceAttemptFailed(_logger, attempt, exception);
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
                }
                catch (Exception exception)
                {
                    BeanBotLog.FortuneResponseReplaceFailed(_logger, botResponse.Id, exception);
                }
            }
        }
    }
}
