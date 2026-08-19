using BeanBot.Services;
using Discord.WebSocket;

namespace BeanBot.EventHandlers;

public sealed class EditMessageHandler : IDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly EditMessageEventServices _editMessageEventService;
    private bool _initialized;

    public EditMessageHandler(
        DiscordSocketClient discordClient,
        EditMessageEventServices editMessageEventService)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _editMessageEventService = editMessageEventService ?? throw new ArgumentNullException(nameof(editMessageEventService));
    }

    public void InitializeEventListener()
    {
        if (_initialized)
        {
            return;
        }

        _discordClient.MessageUpdated += _editMessageEventService.HandleUpdate;
        _initialized = true;
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _discordClient.MessageUpdated -= _editMessageEventService.HandleUpdate;
        _initialized = false;
    }
}
