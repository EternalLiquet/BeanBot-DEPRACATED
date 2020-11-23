using BeanBot.Services;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace BeanBot.EventHandlers
{
    public class EditMessageHandler
    {
        private DiscordSocketClient _discordClient;
        private EditMessageEventServices _editMessageEventService;

        public EditMessageHandler(DiscordSocketClient discordClient)
        {
            _discordClient = discordClient;
            _editMessageEventService = new EditMessageEventServices();
        }

        public void InitializeEventListener()
        {
            _discordClient.MessageUpdated += _editMessageEventService.HandleUpdate;
        }
    }
}
