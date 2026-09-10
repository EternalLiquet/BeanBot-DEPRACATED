using BeanBot.Logging;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

internal sealed class NewMemberHandler : IDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly LogHandler _logHandler;
    private readonly NewMemberWelcomeService _welcomeService;
    private readonly ILogger<NewMemberHandler> _logger;
    private bool _initialized;

    public NewMemberHandler(
        DiscordSocketClient client,
        LogHandler logHandler,
        NewMemberWelcomeService welcomeService,
        ILogger<NewMemberHandler> logger)
    {
        _discordClient = client ?? throw new ArgumentNullException(nameof(client));
        _logHandler = logHandler ?? throw new ArgumentNullException(nameof(logHandler));
        _welcomeService = welcomeService ?? throw new ArgumentNullException(nameof(welcomeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void InitializeNewMembers()
    {
        if (_initialized)
        {
            return;
        }

        BeanBotLog.NewMemberHandlerInitializing(_logger);
        _discordClient.UserJoined += HandleUserJoinedAsync;
        _discordClient.UserJoined += _logHandler.LogNewMember;
        _initialized = true;
    }

    private Task HandleUserJoinedAsync(SocketGuildUser user)
    {
        _welcomeService.TryEnqueue(user.Id, user.IsBot);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _discordClient.UserJoined -= HandleUserJoinedAsync;
        _discordClient.UserJoined -= _logHandler.LogNewMember;
        _initialized = false;
    }
}
