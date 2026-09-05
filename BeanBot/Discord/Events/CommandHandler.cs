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
    private readonly LegacyCommandFeedbackResponder _feedbackResponder;
    private readonly ILogger<CommandHandler> _logger;
    private readonly object _lifecycleGate = new();
    private readonly LegacyCommandExecutionCoordinator _executionCoordinator = new();
    private Task<LegacyCommandDrainResult>? _stopTask;
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
        _feedbackResponder = _services.GetRequiredService<LegacyCommandFeedbackResponder>();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fortuneAnswers = _services.GetRequiredService<FortuneAnswerStore>();
        _messageWaiter = _services.GetRequiredService<DiscordMessageWaiter>();
        BeanBotLog.CommandHandlerCreated(_logger);
    }

    public async Task InitializeCommandsAsync()
    {
        lock (_lifecycleGate)
        {
            if (_initialized || _executionCoordinator.IsStopping)
            {
                return;
            }
        }

        BeanBotLog.CommandsInstalling(_logger);
        await _commandService.AddModulesAsync(assembly: Assembly.GetEntryAssembly() ?? typeof(CommandHandler).Assembly,
                                              services: _services);

        lock (_lifecycleGate)
        {
            if (_initialized || _executionCoordinator.IsStopping)
            {
                return;
            }

            _discordClient.MessageReceived += HandleCommandAsync;
            _commandService.CommandExecuted += _logHandler.LogCommands;
            _commandService.CommandExecuted += _feedbackResponder.RespondAsync;
            _initialized = true;
        }
    }

    internal Task<LegacyCommandDrainResult> StopAsync()
    {
        lock (_lifecycleGate)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            _executionCoordinator.StopAdmission();
            UnsubscribeCore();
            _stopTask = DrainCommandsAsync();
            return _stopTask;
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            _executionCoordinator.StopAdmission();
            UnsubscribeCore();
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

        var admissionResult = await _executionCoordinator.TryExecuteAsync(() =>
        {
            var context = new SocketCommandContext(_discordClient, discordMessage);
            return _commandService.ExecuteAsync(
                context: context,
                argPos: argPos,
                services: _services);
        });

        if (admissionResult == LegacyCommandAdmissionResult.RejectedCapacity)
        {
            LegacyCommandLog.CapacityRejected(
                _logger,
                _executionCoordinator.ActiveExecutionCount,
                _executionCoordinator.MaximumConcurrentExecutions);
        }
        else if (admissionResult == LegacyCommandAdmissionResult.RejectedStopping)
        {
            LegacyCommandLog.ShutdownRejected(_logger);
        }
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

    private async Task<LegacyCommandDrainResult> DrainCommandsAsync()
    {
        var result = await _executionCoordinator.DrainAsync(
            exception => LegacyCommandLog.LateFailure(_logger, exception));
        if (!result.IsDrained)
        {
            LegacyCommandLog.DrainTimedOut(
                _logger,
                result.SurvivingExecutionCount,
                LegacyCommandExecutionCoordinator.DefaultDrainTimeout);
        }

        return result;
    }

    private void UnsubscribeCore()
    {
        if (!_initialized)
        {
            return;
        }

        _discordClient.MessageReceived -= HandleCommandAsync;
        _commandService.CommandExecuted -= _feedbackResponder.RespondAsync;
        _commandService.CommandExecuted -= _logHandler.LogCommands;
        _initialized = false;
    }
}
