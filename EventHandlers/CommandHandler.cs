using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace BeanBot.EventHandlers
{
    public class CommandHandler
    {
        private readonly DiscordSocketClient _discordClient;
        private readonly CommandService _commandService;

        public CommandHandler(DiscordSocketClient discordClient, CommandService commandService)
        {
            this._discordClient = discordClient;
            this._commandService = commandService;
        }

        public async Task InstallCommandsAsync() 
        {
            _discordClient.MessageReceived += HandleCommandAsync;
            await _commandService.AddModulesAsync(assembly: Assembly.GetEntryAssembly(),
                                                  services: null);
        }

        private async Task HandleCommandAsync(SocketMessage messageEvent)
        {
            var discordMessage = messageEvent as SocketUserMessage;
            if (MessageIsSystemMessage(discordMessage)) return;
        }

        private bool MessageIsSystemMessage(SocketUserMessage discordMessage)
        {
            if (discordMessage == null)
                return true;
            else
                return false;
        }
    }
}
