using System.Diagnostics.CodeAnalysis;
using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;

using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace BeanBot.Tests.Discord.Events;

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
        var handler = new PunHandler(
            client,
            options,
            new UnavailablePunProvider(),
            NullLogger<PunHandler>.Instance);

        await handler.DisposeAsync();
        await handler.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => handler.Start());
    }

    private sealed class UnavailablePunProvider : IPunProvider
    {
        public bool TryGetRandomPun([NotNullWhen(true)] out string? pun)
        {
            pun = null;
            return false;
        }
    }
}
