using BeanBot.Attributes;
using BeanBot.Entities;
using BeanBot.Services;
using BeanBot.Repository;
using Discord;
using Discord.Addons.Interactive;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.Modules
{
    [Name("Administrative Commands")]
    public class AdministrativeModule : InteractiveBase
    {
        private readonly RoleReactService roleReactService = new RoleReactService(new RoleReactRepository());

        [Command("role setting", RunMode = RunMode.Async)]
        [Summary("Will create a message for auto-role based on reactions")]
        [Alias("rolesetting", "role settings", "rolesettings")]
        [Remarks("role setting")]
        [RequireGuild]
        [RequireUserPermission(GuildPermission.ManageRoles)]
        public async Task RoleSetting()
        {
            await Task.Factory.StartNew(() => { _ = InvokeRoleSettingsAsync(); });
        }

        public async Task InvokeRoleSettingsAsync()
        {
            List<IMessage> messagesInInteraction = new List<IMessage>();
            try
            {

                messagesInInteraction.Add(Context.Message);
                List<RoleEmotePair> roleEmotePair = new List<RoleEmotePair>();
                messagesInInteraction.Add(await ReplyAsync("How many roles do you wish to configure?"));
                var amountOfRoles = await NextMessageAsync(timeout: TimeSpan.FromSeconds(60));
                if (!await IsNumberOfRolesNotNullAndValid(messagesInInteraction, amountOfRoles)) return;
                for (int i = 0; i < int.Parse(amountOfRoles.Content); i++)
                {
                    messagesInInteraction.Add(await ReplyAsync("Which role would you like to set up?"));
                    var roleToSetup = await NextMessageAsync(timeout: TimeSpan.FromSeconds(60));
                    var roleToAdd = (roleToSetup.Channel as SocketTextChannel).Guild.Roles.First(role => roleToSetup.Content.ToString().ToLower().Trim() == role.Name.ToString().ToLower().Trim());
                    if (!await IsRoleNotNullAndValid(messagesInInteraction, roleToSetup, roleToAdd)) return;
                    messagesInInteraction.Add(await ReplyAsync($"Which emote would you like to set up with the role {roleToSetup.Content}"));
                    var emoteToSetup = await NextMessageAsync(timeout: TimeSpan.FromSeconds(60));
                    var emoteToAdd = (emoteToSetup.Channel as SocketTextChannel).Guild.Emotes.FirstOrDefault(emote => emoteToSetup.Content.Contains(emote.Name));
                    if (!await IsEmoteNotNullAndValid(messagesInInteraction, emoteToSetup, emoteToAdd)) return;
                    if (roleEmotePair.Find(role => role.roleId == roleToAdd.Id.ToString()) != null || roleEmotePair.Find(emote => emote.emojiId == emoteToAdd.Id.ToString()) != null)
                    {
                        messagesInInteraction.Add(await ReplyAsync("You seem to have entered a role or emote that is already being setup, please try again"));
                        return;
                    }
                    else
                    {
                        roleEmotePair.Add(new RoleEmotePair(roleToAdd.Id.ToString(), emoteToAdd.Id.ToString()));
                    }
                }
                messagesInInteraction.Add(await ReplyAsync("Please label this group of roles (i.e Games, Position, NSFW, etc)"));
                var roleGroupLabel = await NextMessageAsync();
                if (!await IsRoleGroupLabelNotNull(messagesInInteraction, roleGroupLabel)) return;
                var messageToListen = await CreateMessageToListen(roleEmotePair, roleGroupLabel.Content);
                await roleReactService.SaveRoleSettings(roleEmotePair, messageToListen);
            }
            finally
            {
                await CleanUpMessagesAfterFiveSeconds(messagesInInteraction);
            }
        }

        private async Task<bool> IsRoleGroupLabelNotNull(List<IMessage> messagesInInteraction, SocketMessage roleGroupLabel)
        {
            if (roleGroupLabel != null)
            {
                messagesInInteraction.Add(roleGroupLabel);
                return true;
            }
            else
            {
                messagesInInteraction.Add(await ReplyAsync("Time has expired, please try again"));
                return false;
            }
        }

        private async Task<IMessage> CreateMessageToListen(List<RoleEmotePair> roleEmotePair, string roleGroupLabel)
        {
            EmbedBuilder roleEmbed = new EmbedBuilder();
            foreach (var pair in roleEmotePair)
            {
                var emote = Context.Guild.Emotes.FirstOrDefault(e => e.Id.ToString() == pair.emojiId);
                roleEmbed.AddField(field =>
                {
                    field.Name = $"{emote}";
                    field.Value = $"<@&{pair.roleId}>";
                    field.IsInline = true;
                });
            }
            roleEmbed.WithFooter(footer => footer.Text = $"Role Group: {roleGroupLabel}");
            var finishedEmbed = roleEmbed.Build();
            var messageToListen = await ReplyAsync(embed: finishedEmbed);
            foreach (var pair in roleEmotePair)
            {
                var emote = Context.Guild.Emotes.FirstOrDefault(e => e.Id.ToString() == pair.emojiId);
                await messageToListen.AddReactionAsync(emote);
                Thread.Sleep(2000);
            }
            return messageToListen;
        }

        private async Task<bool> IsEmoteNotNullAndValid(List<IMessage> messagesInInteraction, SocketMessage emoteToSetup, Emote emote)
        {
            if (emoteToSetup != null && emote != null)
            {
                messagesInInteraction.Add(emoteToSetup);
                return true;
            }
            else if (emoteToSetup != null && emote == null)
            {
                messagesInInteraction.Add(await ReplyAsync($"The emote {emoteToSetup.Content} does not exist, please try again"));
                messagesInInteraction.Add(emoteToSetup);
                return false;
            }
            else
            {
                messagesInInteraction.Add(await ReplyAsync("Time has expired, please try again"));
                return false;
            }
        }

        private async Task<bool> IsRoleNotNullAndValid(List<IMessage> messagesInInteraction, SocketMessage roleToSetup, SocketRole role)
        {
            if (roleToSetup != null && role != null)
            {
                messagesInInteraction.Add(roleToSetup);
                return true;
            }
            else if (roleToSetup != null && role == null)
            {
                messagesInInteraction.Add(await ReplyAsync($"The role {roleToSetup.Content} does not exist, please try again"));
                messagesInInteraction.Add(roleToSetup);
                return false;
            }
            else
            {
                messagesInInteraction.Add(await ReplyAsync("Time has expired, please try again"));
                return false;
            }
        }

        private async Task<bool> IsNumberOfRolesNotNullAndValid(List<IMessage> messagesInInteraction, SocketMessage amountOfRoles)
        {
            if (amountOfRoles != null && int.TryParse(amountOfRoles.Content, out _))
            {
                messagesInInteraction.Add(amountOfRoles);
                return true;
            }
            else if (amountOfRoles != null && !int.TryParse(amountOfRoles.Content, out _))
            {
                messagesInInteraction.Add(await ReplyAsync($"{amountOfRoles.Content} is not a number"));
                messagesInInteraction.Add(amountOfRoles);
                return false;
            }
            else
            {
                messagesInInteraction.Add(await ReplyAsync("Time has expired, please try again"));
                return false;
            }
        }

        private async Task CleanUpMessagesAfterFiveSeconds(List<IMessage> messagesInInteraction)
        {
            Thread.Sleep(5000);
            IEnumerable<IMessage> filteredMessages = messagesInInteraction;
            await (Context.Channel as ITextChannel).DeleteMessagesAsync(filteredMessages);
        }
    }
}
