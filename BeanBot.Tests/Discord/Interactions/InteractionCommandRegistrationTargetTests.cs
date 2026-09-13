using BeanBot.Discord.Interactions;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class InteractionCommandRegistrationTargetTests
{
    [Fact]
    public async Task GlobalTarget_UsesOnlyGlobalReconciliationWithDeleteMissing()
    {
        var globalCalls = 0;
        var globalDeleteMissing = false;
        var guildCalls = 0;

        await InteractionCommandRegistrationTarget.Global.RegisterAsync(
            deleteMissing =>
            {
                globalCalls++;
                globalDeleteMissing = deleteMissing;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                guildCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, globalCalls);
        Assert.True(globalDeleteMissing);
        Assert.Equal(0, guildCalls);
        Assert.Equal("Global", InteractionCommandRegistrationTarget.Global.ScopeName);
        Assert.Null(InteractionCommandRegistrationTarget.Global.GuildId);
    }

    [Fact]
    public async Task GuildTarget_UsesOnlyExactGuildReconciliationWithDeleteMissing()
    {
        const ulong expectedGuildId = 987654321;
        var globalCalls = 0;
        var guildCalls = 0;
        ulong? actualGuildId = null;
        var guildDeleteMissing = false;
        var target = InteractionCommandRegistrationTarget.ForGuild(expectedGuildId);

        await target.RegisterAsync(
            _ =>
            {
                globalCalls++;
                return Task.CompletedTask;
            },
            (guildId, deleteMissing) =>
            {
                guildCalls++;
                actualGuildId = guildId;
                guildDeleteMissing = deleteMissing;
                return Task.CompletedTask;
            });

        Assert.Equal(0, globalCalls);
        Assert.Equal(1, guildCalls);
        Assert.Equal((ulong?)expectedGuildId, actualGuildId);
        Assert.True(guildDeleteMissing);
        Assert.Equal("Guild", target.ScopeName);
        Assert.Equal((ulong?)expectedGuildId, target.GuildId);
    }

    [Fact]
    public void FromGuildId_MapsNullToGlobalAndRejectsZeroGuild()
    {
        Assert.Same(
            InteractionCommandRegistrationTarget.Global,
            InteractionCommandRegistrationTarget.FromGuildId(null));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InteractionCommandRegistrationTarget.FromGuildId(0));
    }

    [Fact]
    public async Task GuildTarget_ConcurrentOwnerCallsShareOneInFlightRequest()
    {
        const ulong expectedGuildId = 123456789;
        var calls = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = InteractionCommandRegistrationTarget.ForGuild(expectedGuildId);
        var registration = new InteractionCommandRegistration(
            () => target.RegisterAsync(
                _ => throw new InvalidOperationException("global registration must not run"),
                (guildId, deleteMissing) =>
                {
                    Assert.Equal(expectedGuildId, guildId);
                    Assert.True(deleteMissing);
                    Interlocked.Increment(ref calls);
                    return completion.Task;
                }),
            TimeSpan.FromSeconds(1));

        var first = registration.EnsureRegisteredAsync();
        var second = registration.EnsureRegisteredAsync();
        completion.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        Assert.False(await registration.EnsureRegisteredAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GuildTarget_CompletedFailureRetriesSameScope()
    {
        const ulong expectedGuildId = 123456789;
        var calls = 0;
        var target = InteractionCommandRegistrationTarget.ForGuild(expectedGuildId);
        var registration = new InteractionCommandRegistration(
            () => target.RegisterAsync(
                _ => throw new InvalidOperationException("global registration must not run"),
                (guildId, deleteMissing) =>
                {
                    Assert.Equal(expectedGuildId, guildId);
                    Assert.True(deleteMissing);
                    calls++;
                    return calls == 1
                        ? Task.FromException(new InvalidOperationException("first guild attempt failed"))
                        : Task.CompletedTask;
                }),
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registration.EnsureRegisteredAsync());
        var retried = await registration.EnsureRegisteredAsync();

        Assert.True(retried);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GuildTarget_TimeoutKeepsSingleAttemptAndStopClosesAdmission()
    {
        const ulong expectedGuildId = 123456789;
        var calls = 0;
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = InteractionCommandRegistrationTarget.ForGuild(expectedGuildId);
        var registration = new InteractionCommandRegistration(
            () => target.RegisterAsync(
                _ => throw new InvalidOperationException("global registration must not run"),
                (guildId, deleteMissing) =>
                {
                    Assert.Equal(expectedGuildId, guildId);
                    Assert.True(deleteMissing);
                    calls++;
                    return stalled.Task;
                }),
            TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TimeoutException>(() => registration.EnsureRegisteredAsync());
        var secondWaiter = registration.EnsureRegisteredAsync();
        Assert.Equal(1, calls);

        stalled.SetResult();
        Assert.True(await secondWaiter);
        await registration.StopAsync(TimeSpan.FromMilliseconds(25));

        Assert.False(await registration.EnsureRegisteredAsync());
        Assert.Equal(1, calls);
    }
}
