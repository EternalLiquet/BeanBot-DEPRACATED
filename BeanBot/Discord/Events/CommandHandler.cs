using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BeanBot.Discord;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Messaging;
using BeanBot.Logging;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

public sealed class CommandHandler : IDisposable
{
    internal enum CommandPrefixKind
    {
        None,
        Succ,
        Mention,
        Percent
    }

    internal enum CommandMessageRoute
    {
        Ignore,
        PublishToMessageWaiter,
        ExecuteCommand
    }

    private readonly DiscordSocketClient _discordClient;
    private readonly CommandService _commandService;
    private readonly IServiceProvider _services;
    private readonly FortuneAnswerStore _fortuneAnswers;
    private readonly DiscordMessageWaiter _messageWaiter;
    private readonly LogHandler _logHandler;
    private readonly ILogger<CommandHandler> _logger;
    private bool _initialized;

    public CommandHandler(
        DiscordSocketClient discordClient,
        CommandService commandService,
        IServiceProvider services,
        LogHandler logHandler,
        ILogger<CommandHandler> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logHandler = logHandler ?? throw new ArgumentNullException(nameof(logHandler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fortuneAnswers = _services.GetRequiredService<FortuneAnswerStore>();
        _messageWaiter = _services.GetRequiredService<DiscordMessageWaiter>();
        BeanBotLog.CommandHandlerCreated(_logger);
    }

    public async Task InitializeCommandsAsync()
    {
        if (_initialized)
        {
            return;
        }

        BeanBotLog.CommandsInstalling(_logger);
        _discordClient.MessageReceived += HandleCommandAsync;
        _commandService.CommandExecuted += _logHandler.LogCommands;
        await _commandService.AddModulesAsync(assembly: Assembly.GetEntryAssembly() ?? typeof(CommandHandler).Assembly,
                                              services: _services);
        _initialized = true;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            _discordClient.MessageReceived -= HandleCommandAsync;
            _commandService.CommandExecuted -= _logHandler.LogCommands;
            _initialized = false;
        }
    }

    internal async Task HandleCommandAsync(SocketMessage messageEvent)
    {
        var discordMessage = messageEvent as SocketUserMessage;
        if (MessageIsSystemMessage(discordMessage))
        {
            return; // Return and ignore if the message is a Discord system message.
        }

        var argPos = 0;
        var commandPrefix = GetCommandPrefix(discordMessage, ref argPos);

        if (discordMessage.Author.Id == BotOwner.DiscordUserId &&
            discordMessage.Content.Contains("queue8", StringComparison.OrdinalIgnoreCase))
        {
            _fortuneAnswers.Queue(
                discordMessage.Author.Id,
                discordMessage.Content.Contains("yes", StringComparison.OrdinalIgnoreCase));
        }

        var route = ResolveMessageRoute(
            isSystemMessage: false,
            messageEvent.Author.IsBot,
            commandPrefix);
        if (route == CommandMessageRoute.PublishToMessageWaiter)
        {
            _messageWaiter.TryPublish(messageEvent);
            return;
        }

        if (route == CommandMessageRoute.Ignore)
        {
            return;
        }

        var context = new SocketCommandContext(_discordClient, discordMessage);
        await _commandService.ExecuteAsync(
            context: context,
            argPos: argPos,
            services: _services);
    }

    internal CommandPrefixKind GetCommandPrefix(SocketUserMessage discordMessage, ref int argPos)
    {
        if (discordMessage.HasStringPrefix("succ ", ref argPos, StringComparison.OrdinalIgnoreCase))
        {
            return CommandPrefixKind.Succ;
        }

        if (discordMessage.HasMentionPrefix(_discordClient.CurrentUser, ref argPos))
        {
            return CommandPrefixKind.Mention;
        }

        return discordMessage.HasCharPrefix('%', ref argPos)
            ? CommandPrefixKind.Percent
            : CommandPrefixKind.None;
    }

    internal static CommandMessageRoute ResolveMessageRoute(
        bool isSystemMessage,
        bool isBot,
        CommandPrefixKind commandPrefix)
    {
        if (isSystemMessage || isBot)
        {
            return CommandMessageRoute.Ignore;
        }

        return commandPrefix == CommandPrefixKind.None
            ? CommandMessageRoute.PublishToMessageWaiter
            : CommandMessageRoute.ExecuteCommand;
    }

    internal static bool MessageIsSystemMessage([NotNullWhen(false)] SocketUserMessage? discordMessage)
        => discordMessage == null;
}
