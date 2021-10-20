using BeanBot.Entities;
using BeanBot.Repository;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    public class RoleReactService
    {
        private readonly RoleReactRepository _roleReactRepository;
        private readonly DiscordSocketClient _client;
        private List<RoleSettings> roleSettings;

        public RoleReactService(RoleReactRepository roleReactRepository) 
        {
            this._roleReactRepository = roleReactRepository;
            roleSettings = this.GetAllRecentRoleSettings().Result;
        }

        public RoleReactService(RoleReactRepository roleReactRepository, DiscordSocketClient client)
        {
            this._roleReactRepository = roleReactRepository;
            this._client = client;
            roleSettings = this.GetAllRecentRoleSettings().Result;
        }

        public async Task HandleReact(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            try
            {

                var cachedMessage = await message.GetOrDownloadAsync();
                Console.WriteLine(_client.CurrentUser.Id);
                Console.WriteLine(cachedMessage.Author.Id);
                Console.WriteLine(cachedMessage.Author.Id == reaction.UserId);
                if (cachedMessage.Author.Id != _client.CurrentUser.Id) return; //If it isn't Bean Bot's message then we don't care about it.
                if (cachedMessage.Author.Id == reaction.UserId) return; //If the bot is the one reacting, we ignore this too
                if (roleSettings == null || roleSettings.Count == 0)
                {
                    roleSettings = await this.GetAllRoleSettings();
                    Console.WriteLine(roleSettings.FirstOrDefault().ToString());
                    if (roleSettings.Count == 0) return;
                }
                var roleSetting = roleSettings.Find(setting => setting.messageId == message.Id.ToString()) ?? await this.GetRoleSetting(cachedMessage);
                if (roleSetting == null) return;
                var guild = (channel as SocketTextChannel).Guild as IGuild;
                var user = await guild.GetUserAsync(reaction.UserId, CacheMode.AllowDownload);
                var emojiId = (reaction.Emote as Emote).Id;
                var roleId = roleSetting.roleEmotePair.Find(pair => pair.emojiId == emojiId.ToString()).roleId;
                var role = guild.Roles.Where(guildRoles => guildRoles.Id.ToString() == roleId).FirstOrDefault();
                Console.WriteLine("I've reached here, no issues");
                await user.AddRoleAsync(role);
            }
            catch (Exception e)
            {
                Log.Error(e.StackTrace);
                Log.Error(e.Message);
            }
        }

        public async Task SaveRoleSettings(List<RoleEmotePair> roleEmotePair, IMessage messageToListen)
        {
            RoleSettings roleSettings = new RoleSettings(
                roleEmotePair, 
                (messageToListen.Channel as SocketTextChannel).Guild.Id.ToString(),
                messageToListen.Channel.Id.ToString(),
                messageToListen.Id.ToString()
                );
            await _roleReactRepository.InsertNewRoleSettings(roleSettings);
        }

        public async Task<List<RoleSettings>> GetAllRoleSettings()
        {
            return await _roleReactRepository.GetAllRoleSettings();
        }

        public async Task<List<RoleSettings>> GetAllRecentRoleSettings()
        {
            return await _roleReactRepository.GetRecentRoleSettings();
        }

        public async Task<RoleSettings> GetRoleSetting(IUserMessage message)
        {
            return await _roleReactRepository.GetRoleSetting(message);
        }
    }
}
