using System.Diagnostics.CodeAnalysis;
using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;

using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task ThirdMessageCancellationPropagatesWithoutErrorLog()
    {
        using var cancellation = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var sends = 0;

        var posting = PunHandler.SendPunMessagesAsync(
            (_, _) =>
            {
                sends++;
                if (sends == 3)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(cancellation.Token);
                }

                return Task.CompletedTask;
            },
            "test pun",
            new RequestOptions { CancelToken = cancellation.Token },
            logger,
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => posting);
        Assert.Equal(3, sends);
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Error);
    }

    private sealed class UnavailablePunProvider : IPunProvider
    {
        public bool TryGetRandomPun([NotNullWhen(true)] out string? pun)
        {
            pun = null;
            return false;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
