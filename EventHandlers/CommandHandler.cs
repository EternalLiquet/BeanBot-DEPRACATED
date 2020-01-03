using Discord.Commands;
using Discord.WebSocket;

using Serilog;

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
            Log.Information("Instantiating Command Handler");
            this._discordClient = discordClient;
            this._commandService = commandService;
        }

        public async Task InstallCommandsAsync() 
        {
            Log.Information("Installing Commands");
            _discordClient.MessageReceived += HandleCommandAsync;
            await _commandService.AddModulesAsync(assembly: Assembly.GetEntryAssembly(),
                                                  services: null);
        }

        private async Task HandleCommandAsync(SocketMessage messageEvent)
        {
            var discordMessage = messageEvent as SocketUserMessage;
            if (MessageIsSystemMessage(discordMessage)) return;
            int argPos = 0; 
            if(!(discordMessage))
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
