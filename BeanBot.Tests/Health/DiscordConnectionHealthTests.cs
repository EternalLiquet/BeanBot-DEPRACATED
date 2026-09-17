using BeanBot.Health;
using Discord.WebSocket;
using Xunit;

namespace BeanBot.Tests.Health;

public class DiscordConnectionHealthTests
{
    [Fact]
    public void InitialSnapshot_HasNoDisconnectReason()
    {
        using var discordClient = new DiscordSocketClient();
        var snapshot = new DiscordConnectionHealth().CreateSnapshot(discordClient);

        Assert.Null(snapshot.MostRecentDisconnectReason);
        Assert.False(snapshot.IsHealthy);
    }

    [Fact]
    public void InitialMetricsSnapshot_HasNoTransitions()
    {
        using var discordClient = new DiscordSocketClient();
        var snapshot = new DiscordConnectionHealth().CreateMetricsSnapshot(discordClient);

        Assert.False(snapshot.IsReady);
        Assert.Equal(0, snapshot.ReadyTransitionCount);
        Assert.Equal(0, snapshot.DisconnectTransitionCount);
        Assert.Null(snapshot.LastReadyAtUtc);
        Assert.Null(snapshot.LastDisconnectedAtUtc);
    }

    [Fact]
    public void MarkDisconnected_NullExceptionUsesFallbackReason()
    {
        using var discordClient = new DiscordSocketClient();
        var health = new DiscordConnectionHealth();

        health.MarkDisconnected(null);
        var snapshot = health.CreateSnapshot(discordClient);

        Assert.Equal("Discord gateway disconnected.", snapshot.MostRecentDisconnectReason);
    }

    [Fact]
    public void MarkReady_PreservesMostRecentDisconnectReasonForDiagnostics()
    {
        using var discordClient = new DiscordSocketClient();
        var health = new DiscordConnectionHealth();
        health.MarkDisconnected(new InvalidOperationException("Temporary DNS failure"));

        health.MarkReady();
        var snapshot = health.CreateSnapshot(discordClient);

        Assert.Equal("Temporary DNS failure", snapshot.MostRecentDisconnectReason);
        Assert.Null(snapshot.UnhealthySinceAtUtc);
    }

    [Fact]
    public void MarkDisconnected_ReplacesMostRecentDisconnectReason()
    {
        using var discordClient = new DiscordSocketClient();
        var health = new DiscordConnectionHealth();
        health.MarkDisconnected(new InvalidOperationException("First failure"));

        health.MarkDisconnected(new InvalidOperationException("Latest failure"));
        var snapshot = health.CreateSnapshot(discordClient);

        Assert.Equal("Latest failure", snapshot.MostRecentDisconnectReason);
    }

    [Fact]
    public void RepeatedGatewayCallbacks_CountOnlyStateTransitions()
    {
        using var discordClient = new DiscordSocketClient();
        var health = new DiscordConnectionHealth();

        health.MarkReady();
        health.MarkReady();
        health.MarkDisconnected(new InvalidOperationException("private failure text"));
        health.MarkDisconnected(new InvalidOperationException("new private failure text"));
        health.MarkReady();

        var snapshot = health.CreateMetricsSnapshot(discordClient);

        Assert.Equal(2, snapshot.ReadyTransitionCount);
        Assert.Equal(1, snapshot.DisconnectTransitionCount);
        Assert.NotNull(snapshot.LastReadyAtUtc);
        Assert.NotNull(snapshot.LastDisconnectedAtUtc);
    }
}
