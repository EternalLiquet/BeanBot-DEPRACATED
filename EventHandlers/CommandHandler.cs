using BeanBot.Util;

using Discord;
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

        public async Task InitializeCommandsAsync() 
        {
            Log.Information("Installing Commands");
            _discordClient.MessageReceived += HandleCommandAsync;
            _commandService.CommandExecuted += OnCommandExecutedAsync;
            await _commandService.AddModulesAsync(assembly: Assembly.GetEntryAssembly(),
                                                  services: null);
        }

        public async Task OnCommandExecutedAsync(Optional<CommandInfo> command, ICommandContext context, IResult result)
        {
            
        }

        private async Task HandleCommandAsync(SocketMessage messageEvent)
        {
            var discordMessage = messageEvent as SocketUserMessage;
            if (MessageIsSystemMessage(discordMessage)) 
                return; //Return and ignore if the message is a discord system message
            int argPos = 0;
            if (!MessageHasCommandPrefix(discordMessage, ref argPos) ||
                messageEvent.Author.IsBot)
                return; //Return and ignore if the discord message does not have the command prefixes or if the author of the message is a bot
            var context = new SocketCommandContext(_discordClient, discordMessage);
            var executionResult = await _commandService.ExecuteAsync(
                context: context,
                argPos: argPos,
                services: null);
            LogResultIfCommandFailed(executionResult);
            _commandService.Log += LogHandler.LogMessages;
        }

        private void LogResultIfCommandFailed(IResult commandServiceResult)
        {
            if (!commandServiceResult.IsSuccess)
            {
                Log.Error(commandServiceResult.ErrorReason);
            }
        }

        private bool MessageHasCommandPrefix(SocketUserMessage discordMessage, ref int argPos)
        {
            return (discordMessage.HasStringPrefix("succ", ref argPos) ||
                            discordMessage.HasMentionPrefix(_discordClient.CurrentUser, ref argPos));
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
