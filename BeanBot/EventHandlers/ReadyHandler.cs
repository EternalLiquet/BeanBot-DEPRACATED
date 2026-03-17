using BeanBot.Services;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace BeanBot.EventHandlers;

public sealed class ReadyHandler(DiscordReadySignal readySignal, ILogger<ReadyHandler> logger) : IReadyGatewayHandler
{
    public ValueTask HandleAsync(ReadyEventArgs arg)
    {
        readySignal.MarkReady();
        logger.LogInformation("Bean Bot connected to Discord as user {UserId}", arg.User.Id);
        return ValueTask.CompletedTask;
    }
}
