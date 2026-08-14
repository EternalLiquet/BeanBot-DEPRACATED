using BeanBot.Configuration;
using BeanBot.EventHandlers;

using Discord.WebSocket;

using Xunit;

namespace BeanBot.Tests.EventHandlers;

public class PunHandlerTests
{
    [Fact]
    public async Task DisposeAsync_IsIdempotentAndPreventsStart()
    {
        using var client = new DiscordSocketClient();
        var options = new BeanBotOptions(
            "token",
            "mongodb://localhost",
            1,
            new Uri("https://example.com/hatoete"),
            new Uri("https://example.com/yoshimaru"),
            HealthCheckOptions.Disabled);
        var handler = new PunHandler(client, options);

        await handler.DisposeAsync();
        await handler.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => handler.Start());
    }
}
