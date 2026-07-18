using BeanBot.Util;
using Discord.WebSocket;
using Serilog;
using System;
using System.Threading.Tasks;

namespace BeanBot.EventHandlers
{
    internal sealed class NewMemberHandler : IDisposable
    {
        private readonly DiscordSocketClient _discordClient;
        private bool _initialized;

        public NewMemberHandler(DiscordSocketClient client)
        {
            _discordClient = client ?? throw new ArgumentNullException(nameof(client));
        }

        public void InitializeNewMembers()
        {
            if (_initialized)
            {
                return;
            }

            Log.Information("Initializing New Member Handler");
            _discordClient.UserJoined += WelcomeNewMemberAsync;
            _discordClient.UserJoined += LogHandler.LogNewMember;
            _initialized = true;
        }

        private static async Task WelcomeNewMemberAsync(SocketGuildUser user)
        {
            if (user.IsBot)
            {
                return;
            }

            try
            {
                var userDmChannel = await user.GetOrCreateDMChannelAsync();
                await userDmChannel.SendMessageAsync("Please read the rules in the Eli's Charter channel. If you agree to these rules and are over the age of 17, please DM one of the moderators with the blue role \"Student Council\" (i.e discount Hatate/Makoto Kikuchi#2351) for full access to the server! (I promise it's worth it)");
                Log.Debug("Sent the welcome message to {UserId}", user.Id);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Could not send the welcome message to {UserId}", user.Id);
            }
        }

        public void Dispose()
        {
            if (!_initialized)
            {
                return;
            }

            _discordClient.UserJoined -= WelcomeNewMemberAsync;
            _discordClient.UserJoined -= LogHandler.LogNewMember;
            _initialized = false;
        }
    }
}
