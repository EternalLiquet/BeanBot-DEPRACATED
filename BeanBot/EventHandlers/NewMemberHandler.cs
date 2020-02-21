using BeanBot.Util;
using Discord.WebSocket;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace BeanBot.EventHandlers
{
    class NewMemberHandler
    {
        private readonly DiscordSocketClient _discordClient;

        public NewMemberHandler(DiscordSocketClient client)
        {
            this._discordClient = client;
        }

        public async Task InitializeNewMembersAsync()
        {
            Log.Information("Initializing New Member Handler");
            _discordClient.UserJoined += async (u) => 
            {
                var userDMChannel = await u.GetOrCreateDMChannelAsync();
                Log.Debug("Successfully created user DM channel");
                await userDMChannel.SendMessageAsync("Please read the rules in the Eli's Charter channel. If you agree to these rules and are over the age of 17, please use command %shine");
                Log.Debug("Successfully sent message");
            };
            _discordClient.UserJoined += LogHandler.LogNewMember;
        }
    }
}
