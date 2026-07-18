using BeanBot.Entities;
using BeanBot.Repository;
using Discord;
using Discord.WebSocket;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    public class RoleReactService
    {
        private readonly RoleReactRepository _roleReactRepository;
        private readonly DiscordSocketClient _client;
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, RoleSettings> _roleSettings = new ConcurrentDictionary<string, RoleSettings>();
        private volatile bool _cacheLoaded;

        public RoleReactService(RoleReactRepository roleReactRepository, DiscordSocketClient client = null)
        {
            _roleReactRepository = roleReactRepository ?? throw new ArgumentNullException(nameof(roleReactRepository));
            _client = client;
        }

        public Task HandleReact(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
            => HandleReactionAsync(message, channel, reaction, addRole: true);

        public Task HandleRemoveReact(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
            => HandleReactionAsync(message, channel, reaction, addRole: false);

        private async Task HandleReactionAsync(
            Cacheable<IUserMessage, ulong> message,
            ISocketMessageChannel channel,
            SocketReaction reaction,
            bool addRole)
        {
            try
            {
                if (_client?.CurrentUser == null || channel is not SocketTextChannel textChannel)
                {
                    return;
                }

                var cachedMessage = await message.GetOrDownloadAsync();
                if (cachedMessage.Author.Id != _client.CurrentUser.Id || reaction.UserId == _client.CurrentUser.Id)
                {
                    return;
                }

                if (reaction.Emote is not Emote customEmote)
                {
                    return;
                }

                var roleSetting = await GetCachedRoleSettingAsync(message.Id, cachedMessage);
                var pair = roleSetting?.roleEmotePair?
                    .FirstOrDefault(candidate => candidate.emojiId == customEmote.Id.ToString());
                if (pair == null || !ulong.TryParse(pair.roleId, out var roleId))
                {
                    return;
                }

                var guild = (IGuild)textChannel.Guild;
                var user = await guild.GetUserAsync(reaction.UserId, CacheMode.AllowDownload);
                var role = guild.Roles.FirstOrDefault(candidate => candidate.Id == roleId);
                if (user == null || role == null)
                {
                    return;
                }

                if (addRole && !user.RoleIds.Contains(roleId))
                {
                    await user.AddRoleAsync(role);
                }
                else if (!addRole && user.RoleIds.Contains(roleId))
                {
                    await user.RemoveRoleAsync(role);
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Failed to {Action} a reaction role for message {MessageId}", addRole ? "add" : "remove", message.Id);
            }
        }

        private async Task<RoleSettings> GetCachedRoleSettingAsync(ulong messageId, IUserMessage message)
        {
            await EnsureCacheLoadedAsync();
            var messageIdText = messageId.ToString();
            if (_roleSettings.TryGetValue(messageIdText, out var cached))
            {
                return cached;
            }

            var roleSetting = await _roleReactRepository.GetRoleSetting(message);
            if (roleSetting != null && !string.IsNullOrWhiteSpace(roleSetting.messageId))
            {
                _roleSettings[roleSetting.messageId] = roleSetting;
            }

            return roleSetting;
        }

        private async Task EnsureCacheLoadedAsync()
        {
            if (_cacheLoaded)
            {
                return;
            }

            await _cacheLock.WaitAsync();
            try
            {
                if (_cacheLoaded)
                {
                    return;
                }

                foreach (var setting in await _roleReactRepository.GetRecentRoleSettings())
                {
                    if (!string.IsNullOrWhiteSpace(setting.messageId))
                    {
                        _roleSettings[setting.messageId] = setting;
                    }
                }
                _cacheLoaded = true;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public async Task SaveRoleSettings(List<RoleEmotePair> roleEmotePair, IMessage messageToListen)
        {
            if (messageToListen.Channel is not SocketTextChannel textChannel)
            {
                throw new InvalidOperationException("Reaction-role settings can only be saved from a guild text channel.");
            }

            var settings = new RoleSettings(
                roleEmotePair,
                textChannel.Guild.Id.ToString(),
                messageToListen.Channel.Id.ToString(),
                messageToListen.Id.ToString());
            await _roleReactRepository.InsertNewRoleSettings(settings);
            _roleSettings[settings.messageId] = settings;
        }
    }
}
