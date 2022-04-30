using BeanBot.Services;
using BeanBot.Repository;
using Discord.WebSocket;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace BeanBot.EventHandlers
{
    public class ReactHandler
    {
        private readonly DiscordSocketClient _discordClient;
        private readonly RoleReactService _roleService;

        public ReactHandler(DiscordSocketClient discordClient, RoleReactService roleReactService)
        {
            Log.Information("Instantiating React Handler");
            this._discordClient = discordClient;
            this._roleService = new RoleReactService(new RoleReactRepository(), _discordClient);
        }

        public async Task InitializeReactDependentServices()
        {
            await InstantiateRoleServices();
        }

        private Task InstantiateRoleServices()
        {
            Log.Information("Instantiating Role Services");
            _ = Task.Factory.StartNew(() => { _discordClient.ReactionAdded += _roleService.HandleReact; });
            _ = Task.Factory.StartNew(() => { _discordClient.ReactionRemoved += _roleService.HandleRemoveReact; });
            return Task.CompletedTask;
        }
    }
}
