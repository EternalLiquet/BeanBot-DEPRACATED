using BeanBot.Attributes;
using BeanBot.Entities;
using BeanBot.Services;
using Discord;
using Discord.Addons.Interactive;
using Discord.Commands;
using Discord.WebSocket;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BeanBot.Modules
{
    [Name("Administrative Commands")]
    public class AdministrativeModule : InteractiveBase
    {
        private const int MaximumRolesPerGroup = 25;
        private static readonly TimeSpan InteractionTimeout = TimeSpan.FromSeconds(60);
        private readonly RoleReactService _roleReactService;
        private readonly DiscordMessageCleanupService _messageCleanupService;

        public AdministrativeModule(
            RoleReactService roleReactService,
            DiscordMessageCleanupService messageCleanupService)
        {
            _roleReactService = roleReactService ?? throw new ArgumentNullException(nameof(roleReactService));
            _messageCleanupService = messageCleanupService ?? throw new ArgumentNullException(nameof(messageCleanupService));
        }

        [Command("role setting", RunMode = RunMode.Async)]
        [Summary("Will create a message for auto-role based on reactions")]
        [Alias("rolesetting", "role settings", "rolesettings")]
        [Remarks("role setting")]
        [RequireGuild]
        [RequireUserPermission(GuildPermission.ManageRoles)]
        [RequireBotPermission(GuildPermission.EmbedLinks)]
        public Task RoleSetting() => InvokeRoleSettingsAsync();

        internal async Task InvokeRoleSettingsAsync()
        {
            var messagesInInteraction = new List<IMessage> { Context.Message };
            try
            {
                var roleEmotePairs = new List<RoleEmotePair>();
                messagesInInteraction.Add(await ReplyAsync($"How many roles do you wish to configure? (1-{MaximumRolesPerGroup})"));
                var amountMessage = await NextMessageAsync(timeout: InteractionTimeout);
                var roleCountResult = await GetRoleCountAsync(messagesInInteraction, amountMessage);
                if (!roleCountResult.Success)
                {
                    return;
                }

                for (var index = 0; index < roleCountResult.RoleCount; index++)
                {
                    messagesInInteraction.Add(await ReplyAsync("Which role would you like to set up?"));
                    var roleMessage = await NextMessageAsync(timeout: InteractionTimeout);
                    var role = await GetRoleAsync(messagesInInteraction, roleMessage);
                    if (role == null)
                    {
                        return;
                    }

                    messagesInInteraction.Add(await ReplyAsync($"Which emote would you like to set up with the role {role.Name}?"));
                    var emoteMessage = await NextMessageAsync(timeout: InteractionTimeout);
                    var emote = await GetEmoteAsync(messagesInInteraction, emoteMessage);
                    if (emote == null)
                    {
                        return;
                    }

                    if (roleEmotePairs.Any(pair => pair.roleId == role.Id.ToString() || pair.emojiId == emote.Id.ToString()))
                    {
                        messagesInInteraction.Add(await ReplyAsync("That role or emote is already being configured. Please start again."));
                        return;
                    }

                    roleEmotePairs.Add(new RoleEmotePair(role.Id.ToString(), emote.Id.ToString()));
                }

                messagesInInteraction.Add(await ReplyAsync("Please label this group of roles (i.e. Games, Position, NSFW, etc)."));
                var labelMessage = await NextMessageAsync(timeout: InteractionTimeout);
                if (labelMessage == null)
                {
                    messagesInInteraction.Add(await ReplyAsync("Time has expired, please try again."));
                    return;
                }

                messagesInInteraction.Add(labelMessage);
                await ReactionRoleSetupTransaction.ExecuteAsync(
                    () => CreateRoleMessageAsync(roleEmotePairs, labelMessage.Content),
                    async messageToListen =>
                    {
                        await AddRoleReactionsAsync(messageToListen, roleEmotePairs);
                        await _roleReactService.SaveRoleSettings(roleEmotePairs, messageToListen);
                    },
                    messageToListen => messageToListen.DeleteAsync(),
                    exception => Log.Warning(
                        exception,
                        "Could not delete incomplete reaction-role message after setup failed"));
            }
            finally
            {
                await CleanUpMessagesAsync(messagesInInteraction);
            }
        }

        private async Task<IUserMessage> CreateRoleMessageAsync(IEnumerable<RoleEmotePair> roleEmotePairs, string roleGroupLabel)
        {
            var pairs = roleEmotePairs.ToList();
            var roleEmbed = new EmbedBuilder();
            foreach (var pair in pairs)
            {
                var emote = Context.Guild.Emotes.First(candidate => candidate.Id.ToString() == pair.emojiId);
                roleEmbed.AddField(emote.ToString(), $"<@&{pair.roleId}>", inline: true);
            }

            roleEmbed.WithFooter(footer => footer.Text = $"Role Group: {roleGroupLabel}");
            return await ReplyAsync(embed: roleEmbed.Build());
        }

        private async Task AddRoleReactionsAsync(IUserMessage messageToListen, IEnumerable<RoleEmotePair> roleEmotePairs)
        {
            var pairs = roleEmotePairs.ToList();
            foreach (var pair in pairs)
            {
                var emote = Context.Guild.Emotes.First(candidate => candidate.Id.ToString() == pair.emojiId);
                await messageToListen.AddReactionAsync(emote);
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        private async Task<(bool Success, int RoleCount)> GetRoleCountAsync(List<IMessage> messages, SocketMessage response)
        {
            if (response == null)
            {
                messages.Add(await ReplyAsync("Time has expired, please try again."));
                return (false, 0);
            }

            messages.Add(response);
            if (!int.TryParse(response.Content, out var roleCount) || roleCount < 1 || roleCount > MaximumRolesPerGroup)
            {
                messages.Add(await ReplyAsync($"Please enter a whole number from 1 to {MaximumRolesPerGroup}."));
                return (false, 0);
            }

            return (true, roleCount);
        }

        private async Task<SocketRole> GetRoleAsync(List<IMessage> messages, SocketMessage response)
        {
            if (response == null)
            {
                messages.Add(await ReplyAsync("Time has expired, please try again."));
                return null;
            }

            messages.Add(response);
            var role = Context.Guild.Roles.FirstOrDefault(candidate =>
                string.Equals(response.Content.Trim(), candidate.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (role == null)
            {
                messages.Add(await ReplyAsync($"The role {response.Content} does not exist. Please start again."));
                return null;
            }

            return role;
        }

        private async Task<Emote> GetEmoteAsync(List<IMessage> messages, SocketMessage response)
        {
            if (response == null)
            {
                messages.Add(await ReplyAsync("Time has expired, please try again."));
                return null;
            }

            messages.Add(response);
            var emote = Context.Guild.Emotes.FirstOrDefault(candidate =>
                response.Content.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase));
            if (emote == null)
            {
                messages.Add(await ReplyAsync($"The emote {response.Content} does not exist. Please start again."));
                return null;
            }

            return emote;
        }

        private async Task CleanUpMessagesAsync(IReadOnlyCollection<IMessage> messages)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (Context.Channel is not ITextChannel textChannel || messages.Count == 0)
            {
                return;
            }

            try
            {
                await _messageCleanupService.DeleteAsync(textChannel, messages);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Could not clean up the reaction-role setup messages");
            }
        }
    }
}
