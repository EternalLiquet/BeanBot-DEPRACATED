using BeanBot.Discord.Events;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Events;

public class EditMessageHandlerTests
{
    [Fact]
    public void TryTrackOperation_CapacityIsBounded()
    {
        using var client = new DiscordSocketClient();
        using var handler = CreateHandler(client);
        var completions = Enumerable.Range(0, EditMessageHandler.MaximumInFlightOperations)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        foreach (var completion in completions)
        {
            Assert.True(handler.TryTrackOperation(_ => completion.Task));
        }

        Assert.False(handler.TryTrackOperation(_ => Task.CompletedTask));
        Assert.True(handler.HasInFlightOperations);

        foreach (var completion in completions)
        {
            completion.SetResult();
        }

        Assert.False(handler.HasInFlightOperations);
    }

    [Fact]
    public async Task StopAsync_CancelsAndDrainsAdmittedOperationAndStopsAdmission()
    {
        using var client = new DiscordSocketClient();
        using var handler = CreateHandler(client);
        CancellationToken observedToken = default;

        Assert.True(handler.TryTrackOperation(async token =>
        {
            observedToken = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));

        await handler.StopAsync(TimeSpan.FromSeconds(1));

        Assert.True(observedToken.IsCancellationRequested);
        Assert.False(handler.HasInFlightOperations);
        Assert.False(handler.TryTrackOperation(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task StopAsync_OperationIgnoringCancellation_TimesOutAndRetainsOwnership()
    {
        using var client = new DiscordSocketClient();
        using var handler = CreateHandler(client);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(handler.TryTrackOperation(_ => completion.Task));

        await Assert.ThrowsAsync<TimeoutException>(
            () => handler.StopAsync(TimeSpan.FromMilliseconds(25)));

        Assert.True(handler.HasInFlightOperations);
        completion.SetResult();
        Assert.False(handler.HasInFlightOperations);
    }

    [Fact]
    public async Task StopAsync_LateDiscordOperation_TimesOutUntilOwnedTaskSettles()
    {
        using var client = new DiscordSocketClient();
        using var handler = CreateHandler(client);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        handler.TrackOwnedDiscordOperation(completion.Task);

        await Assert.ThrowsAsync<TimeoutException>(
            () => handler.StopAsync(TimeSpan.FromMilliseconds(25)));

        Assert.True(handler.HasInFlightOperations);
        completion.SetException(new InvalidOperationException("late Discord failure"));
        Assert.False(handler.HasInFlightOperations);
    }

    [Fact]
    public async Task StopAsync_IsIdempotent()
    {
        using var client = new DiscordSocketClient();
        using var handler = CreateHandler(client);

        handler.InitializeEventListener();
        handler.InitializeEventListener();

        await handler.StopAsync(TimeSpan.FromSeconds(1));
        await handler.StopAsync(TimeSpan.FromSeconds(1));

        Assert.False(handler.HasInFlightOperations);
    }

    private static EditMessageHandler CreateHandler(DiscordSocketClient client)
    {
        var service = new EditMessageEventServices(
            client,
            NullLogger<EditMessageEventServices>.Instance);
        return new EditMessageHandler(
            client,
            service,
            NullLogger<EditMessageHandler>.Instance);
    }
}
