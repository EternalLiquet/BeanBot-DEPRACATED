using Discord;
using Discord.WebSocket;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace BeanBot.Examples
{
    class ExampleMethods
    {
        private DiscordSocketClient _discordClient;
        // Insert _discordClient.MessageUpdated += MessageUpdated; to see use of example
        private async Task MessageUpdated(Cacheable<IMessage, ulong> before, SocketMessage after, ISocketMessageChannel channel)
        {
            // If the message was not in the cache, downloading it will result in getting a copy of `after`.
            var message = await before.GetOrDownloadAsync();
            Log.Information($"{message} -> {after}");
        }

        public string GetChannelTopic(ulong id)
        {
            var channel = _discordClient.GetChannel(81384956881809408) as SocketTextChannel;
            return channel?.Topic;
        }

        public SocketGuildUser GetGuildOwner(SocketChannel channel)
        {
            var guild = (channel as SocketGuildChannel)?.Guild;
            return guild?.Owner;
        }
    }
}
