using Discord;
using Discord.WebSocket;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    public sealed class EditMessageEventServices
    {
        private const string EditWarning = "Do not edit your 8ball requests in my presence, mortal.";
        private readonly DiscordSocketClient _discordClient;

        public EditMessageEventServices(DiscordSocketClient discordClient)
        {
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
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
                Log.Debug("Could not find a cached fortune response for edited message {MessageId}", newMessage.Id);
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

            var commandEnd = commandText.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            var command = commandEnd < 0 ? commandText : commandText.Substring(0, commandEnd);
            return string.Equals(command, "8ball", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(command, "fortune", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task ReplaceResponseAsync(SocketUserMessage botResponse)
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
                    Log.Debug(exception, "Attempt {Attempt} to replace an edited fortune response failed", attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Could not replace the response to edited fortune message {MessageId}", botResponse.Id);
                }
            }
        }
    }
}
