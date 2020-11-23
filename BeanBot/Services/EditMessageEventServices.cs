using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    public class EditMessageEventServices
    {
        public async Task HandleUpdate(Cacheable<IMessage, ulong> oldMessage, SocketMessage newMessage, ISocketMessageChannel messageChannel)
        {
            await oldMessage.DownloadAsync();
            if (oldMessage.Value.Content.StartsWith("%8ball") || oldMessage.Value.Content.StartsWith("succ 8ball"))
            {
                var cachedMessages = messageChannel.CachedMessages;
                var messages = new List<SocketMessage>(cachedMessages);
                Console.WriteLine(messages.Count);
                foreach (var message in messages)
                {
                    Console.WriteLine(message.Content);
                }
                var oldMsgIndex = messages.FindIndex(message => message.Id == newMessage.Id);
                if (messages[oldMsgIndex + 1].Author.IsBot)
                {
                    await ModifyMessage(messages, oldMsgIndex);
                }
            }
        }

        private static async Task ModifyMessage(List<SocketMessage> messages, int oldMsgIndex)
        {
            try
            {
                var eightballmessage = messages[oldMsgIndex + 1] as SocketUserMessage;
                await eightballmessage.ModifyAsync(m => m.Content = "Do not edit your 8ball requests in my presence, mortal.");
            }
            catch
            {
                await ModifyMessage(messages, oldMsgIndex);
            }
        }
    }
}
