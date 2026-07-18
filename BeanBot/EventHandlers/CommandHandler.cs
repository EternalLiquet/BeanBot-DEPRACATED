using BeanBot.Util;

using Discord.Addons.Interactive;
using Discord.Commands;
using Discord.WebSocket;

using Microsoft.Extensions.DependencyInjection;

using Serilog;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace BeanBot.EventHandlers
{
    public sealed class CommandHandler : IDisposable
    {
        private readonly DiscordSocketClient _discordClient;
        private readonly CommandService _commandService;
        private readonly IServiceProvider _services;
        private bool _initialized;

        public CommandHandler(DiscordSocketClient discordClient, CommandService commandService)
        {
            Log.Information("Instantiating Command Handler");
            _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
            _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
            _services = new ServiceCollection()
                .AddSingleton(_discordClient)
                .AddSingleton(_commandService)
                .AddSingleton(new InteractiveService(_discordClient))
                .BuildServiceProvider();
        }

        public async Task InitializeCommandsAsync()
        {
            if (_initialized)
            {
                return;
            }

            Log.Information("Installing Commands");
            _discordClient.MessageReceived += HandleCommandAsync;
            _commandService.CommandExecuted += LogHandler.LogCommands;
            await _commandService.AddModulesAsync(assembly: Assembly.GetEntryAssembly() ?? typeof(CommandHandler).Assembly,
                                                  services: _services);
            _initialized = true;
        }

        public void Dispose()
        {
            if (_initialized)
            {
                _discordClient.MessageReceived -= HandleCommandAsync;
                _commandService.CommandExecuted -= LogHandler.LogCommands;
                _initialized = false;
            }

            (_services as IDisposable)?.Dispose();
        }

        internal async Task HandleCommandAsync(SocketMessage messageEvent)
        {
            var discordMessage = messageEvent as SocketUserMessage;
            if (MessageIsSystemMessage(discordMessage))
            {
                return; //Return and ignore if the message is a discord system message
            }

            int argPos = 0;
            if (discordMessage.Author.Id == BotOwner.DiscordUserId &&
                discordMessage.Content.Contains("queue8", StringComparison.OrdinalIgnoreCase))
            {
                Program.FortuneAnswers.Queue(
                    discordMessage.Author.Id,
                    discordMessage.Content.Contains("yes", StringComparison.OrdinalIgnoreCase));
            }
            if (!MessageHasCommandPrefix(discordMessage, ref argPos) ||
                messageEvent.Author.IsBot)
            {
                return; //Return and ignore if the discord message does not have the command prefixes or if the author of the message is a bot
            }

            var context = new SocketCommandContext(_discordClient, discordMessage);
            await _commandService.ExecuteAsync(
                context: context,
                argPos: argPos,
                services: _services);
        }

        internal bool MessageHasCommandPrefix(SocketUserMessage discordMessage, ref int argPos)
        {
            return (discordMessage.HasStringPrefix("succ ", ref argPos, StringComparison.OrdinalIgnoreCase) ||
                            discordMessage.HasMentionPrefix(_discordClient.CurrentUser, ref argPos) ||
                            discordMessage.HasCharPrefix('%', ref argPos));
        }

        internal bool MessageIsSystemMessage(SocketUserMessage discordMessage)
            => discordMessage == null;
    }
}
