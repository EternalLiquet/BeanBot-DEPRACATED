using BeanBot.Services;
using Discord.WebSocket;
using Serilog;
using System;

namespace BeanBot.EventHandlers
{
    public sealed class ReactHandler : IDisposable
    {
        private readonly DiscordSocketClient _discordClient;
        private readonly RoleReactService _roleService;
        private bool _initialized;

        public ReactHandler(DiscordSocketClient discordClient, RoleReactService roleReactService)
        {
            Log.Information("Instantiating React Handler");
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
            _roleService = roleReactService ?? throw new ArgumentNullException(nameof(roleReactService));
        }

        public void InitializeReactDependentServices()
        {
            if (_initialized)
            {
                return;
            }

            Log.Information("Instantiating Role Services");
            _discordClient.ReactionAdded += _roleService.HandleReact;
            _discordClient.ReactionRemoved += _roleService.HandleRemoveReact;
            _initialized = true;
        }

        public void Dispose()
        {
            if (!_initialized)
            {
                return;
            }

            _discordClient.ReactionAdded -= _roleService.HandleReact;
            _discordClient.ReactionRemoved -= _roleService.HandleRemoveReact;
            _initialized = false;
        }
    }
}
