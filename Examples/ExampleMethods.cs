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


        /*
         * _discordClient.ReactionAdded += ReactionAdded;
         * _discordClient.MessageReceived += seeIfMessageHasKazInIt;
         * 
         */
        private async Task seeIfMessageHasKazInIt(SocketMessage arg)
        {
            var message = arg as SocketUserMessage;
            if (message.Content.ToLower().Contains("kaz") || message.Content.ToLower().Contains("toes"))
            {
                await message.AddReactionAsync(Emote.Parse("<a:kaz:653283406712406036>"));
                await message.Channel.SendMessageAsync("<:smug1:621953428477706240>");
            }
            else
                return;
        }

        private async Task ReactionAdded(Cacheable<IUserMessage, ulong> arg1, ISocketMessageChannel arg2, SocketReaction arg3)
        {
            Log.Information(arg1.ToString());
            Log.Information(_discordClient.CurrentUser.ToString());
            Log.Information(arg3.User.Value.ToString());
            var message = await arg1.GetOrDownloadAsync();
            var socketReaction = arg3.Emote;
            var user = arg3.User.Value;
            if (_discordClient.CurrentUser == message.Author)
                return;
            if (user.ToString() != _discordClient.CurrentUser.ToString())
            {
                await arg2.SendMessageAsync("Hello don't mind me this is a test lmao");
                await arg2.SendMessageAsync($"{socketReaction.Name} react added, reacting with hypersuhaha");
            }
            await message.AddReactionAsync(Emote.Parse("<:hypersuhaha:548280190388797451>"));
        }
    }
}
