using BeanBot.Discord.Interactions;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class InteractionCommandRegistrationTests
{
    [Fact]
    public async Task EnsureRegisteredAsync_AfterSuccess_RegistersOnlyOnce()
    {
        var calls = 0;
        var registration = new InteractionCommandRegistration(
            () =>
            {
                calls++;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        var first = await registration.EnsureRegisteredAsync();
        var second = await registration.EnsureRegisteredAsync();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EnsureRegisteredAsync_ConcurrentCalls_ShareOneRegistration()
    {
        var calls = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new InteractionCommandRegistration(
            () =>
            {
                Interlocked.Increment(ref calls);
                return completion.Task;
            },
            TimeSpan.FromSeconds(1));

        var first = registration.EnsureRegisteredAsync();
        var second = registration.EnsureRegisteredAsync();
        completion.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
    }

    [Fact]
    public async Task EnsureRegisteredAsync_AfterFailure_AllowsRetry()
    {
        var calls = 0;
        var registration = new InteractionCommandRegistration(
            () =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException(new InvalidOperationException("first attempt failed"))
                    : Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.EnsureRegisteredAsync());
        var retried = await registration.EnsureRegisteredAsync();

        Assert.True(retried);
        Assert.Equal(2, calls);
    }
}
