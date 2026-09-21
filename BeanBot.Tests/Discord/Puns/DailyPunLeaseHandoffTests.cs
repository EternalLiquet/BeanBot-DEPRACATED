using System.Diagnostics.CodeAnalysis;
using BeanBot.Configuration;
using BeanBot.Discord.Puns;
using BeanBot.Persistence.Repositories;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Puns;

public sealed class DailyPunLeaseHandoffTests
{
    [Fact]
    public async Task CanceledSendWait_RemainsOwnedUntilUnderlyingDiscordTaskSettles()
    {
        var sendStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        await using var service = CreateService();

        var posting = DailyPunService.SendPunMessagesAsync(
            (_, _) =>
            {
                sendStarted.TrySetResult();
                return sendCompletion.Task;
            },
            "test pun",
            new RequestOptions { CancelToken = cancellation.Token },
            NullLogger<DailyPunService>.Instance,
            TimeSpan.FromSeconds(5),
            service.TrackDiscordOperation);

        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(service.HasActiveDiscordOperation);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => posting);

        Assert.True(service.HasActiveDiscordOperation);

        sendCompletion.TrySetResult();
        await service.WaitForDiscordOperationsAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(service.HasActiveDiscordOperation);
    }

    private static DailyPunService CreateService()
        => new(
            123,
            new StaticPunProvider(),
            new AlwaysAcquiredClaimStore(),
            () => null,
            TimeProvider.System,
            new PunSchedulerOptions(
                TimeSpan.FromMinutes(45),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5)),
            NullLogger<DailyPunService>.Instance,
            DailyPunSchedule.CreateDefault());

    private sealed class StaticPunProvider : IPunProvider
    {
        public bool TryGetRandomPun([NotNullWhen(true)] out string? value)
        {
            value = "test pun";
            return true;
        }
    }

    private sealed class AlwaysAcquiredClaimStore : IDailyPunClaimStore
    {
        public Task<DailyPunClaimResult> TryClaimAsync(
            DateOnly chicagoDate,
            CancellationToken cancellationToken)
            => Task.FromResult(DailyPunClaimResult.Acquired);
    }
}
