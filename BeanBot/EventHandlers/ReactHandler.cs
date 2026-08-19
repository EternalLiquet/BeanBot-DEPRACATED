using BeanBot.Services;
using BeanBot.Util;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.EventHandlers;

public sealed class ReactHandler : IDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly RoleReactService _roleService;
    private readonly ILogger<ReactHandler> _logger;
    private bool _initialized;

    public ReactHandler(
        DiscordSocketClient discordClient,
        RoleReactService roleReactService,
        ILogger<ReactHandler> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _roleService = roleReactService ?? throw new ArgumentNullException(nameof(roleReactService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        BeanBotLog.ReactHandlerCreated(_logger);
    }

    public void InitializeReactDependentServices()
    {
        if (_initialized)
        {
            return;
        }

        BeanBotLog.RoleServicesCreated(_logger);
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
