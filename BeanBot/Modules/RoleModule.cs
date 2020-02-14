using BeanBot.Util;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeanBot.Modules
{
    [Name("Role Commands")]
    public class RoleModule : ModuleBase
    {
        private readonly ulong ILserverId = (ulong)Support.ILServerId;

        [Command("shine")]
        [Summary("Will give you access to the Illinois Livers server")]
        [Remarks("shine")]
        public async Task Shine()
        {
            var user = Context.User;
            var ilServer = await Context.Client.GetGuildAsync(ILserverId);
            var studentRole = ilServer.Roles.FirstOrDefault(role => role.Name.Contains("Student"));
            await ilServer.GetUserAsync(user.Id).Result.AddRoleAsync(studentRole);
            await ReplyAsync("You have successfully been given the Student role, welcome to the Illinois Livers Server!");
        }
    }
}
