using BeanBot.Services;
using Discord.WebSocket;
using System;

namespace BeanBot.EventHandlers
{
    public sealed class EditMessageHandler : IDisposable
    {
        private readonly DiscordSocketClient _discordClient;
        private readonly EditMessageEventServices _editMessageEventService;
        private bool _initialized;

        public EditMessageHandler(DiscordSocketClient discordClient)
        {
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
            _editMessageEventService = new EditMessageEventServices(_discordClient);
        }

        public void InitializeEventListener()
        {
            if (_initialized)
            {
                return;
            }

            _discordClient.MessageUpdated += _editMessageEventService.HandleUpdate;
            _initialized = true;
        }

        public void Dispose()
        {
            if (!_initialized)
            {
                return;
            }

            _discordClient.MessageUpdated -= _editMessageEventService.HandleUpdate;
            _initialized = false;
        }
    }
}
