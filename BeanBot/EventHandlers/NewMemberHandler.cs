using BeanBot.Util;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.EventHandlers;

internal sealed class NewMemberHandler : IDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly LogHandler _logHandler;
    private readonly ILogger<NewMemberHandler> _logger;
    private bool _initialized;

    public NewMemberHandler(
        DiscordSocketClient client,
        LogHandler logHandler,
        ILogger<NewMemberHandler> logger)
    {
        _discordClient = client ?? throw new ArgumentNullException(nameof(client));
        _logHandler = logHandler ?? throw new ArgumentNullException(nameof(logHandler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void InitializeNewMembers()
    {
        if (_initialized)
        {
            return;
        }

        BeanBotLog.NewMemberHandlerInitializing(_logger);
        _discordClient.UserJoined += WelcomeNewMemberAsync;
        _discordClient.UserJoined += _logHandler.LogNewMember;
        _initialized = true;
    }

    private async Task WelcomeNewMemberAsync(SocketGuildUser user)
    {
        if (user.IsBot)
        {
            return;
        }

        try
        {
            var userDmChannel = await user.CreateDMChannelAsync();
            await userDmChannel.SendMessageAsync("Please read the rules in the Eli's Charter channel. If you agree to these rules and are over the age of 17, please DM one of the moderators with the blue role \"Student Council\" (i.e discount Hatate/Makoto Kikuchi#2351) for full access to the server! (I promise it's worth it)");
            BeanBotLog.WelcomeMessageSent(_logger, user.Id);
        }
        catch (Exception exception)
        {
            BeanBotLog.WelcomeMessageFailed(_logger, user.Id, exception);
        }
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _discordClient.UserJoined -= WelcomeNewMemberAsync;
        _discordClient.UserJoined -= _logHandler.LogNewMember;
        _initialized = false;
    }
}
