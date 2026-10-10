using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuEditMutationWaiterTests
{
    [Fact]
    public async Task TimeoutFeedback_DoesNotFinishTrackedMutationBeforeItSettles()
    {
        var mutation = new TaskCompletionSource<RoleMenuEditResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var feedbackSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource();
        var waiting = RoleMenuEditMutationWaiter.WaitAsync(
            mutation.Task, () =>
            {
                feedbackSent.SetResult();
                return Task.CompletedTask;
            }, timeout.Token);

        timeout.Cancel();
        await feedbackSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted);
        mutation.SetResult(new RoleMenuEditResult(RoleMenuEditStatus.Updated, "Updated"));

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TimeoutFeedback_StillWaitsForFaultedMutation()
    {
        var mutation = new TaskCompletionSource<RoleMenuEditResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var feedbackSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource();
        var waiting = RoleMenuEditMutationWaiter.WaitAsync(
            mutation.Task, () =>
            {
                feedbackSent.SetResult();
                return Task.CompletedTask;
            }, timeout.Token);
        timeout.Cancel();
        await feedbackSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted);
        mutation.SetException(new InvalidOperationException("late failure"));

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(mutation.Task.IsFaulted);
    }

    [Fact]
    public async Task SuccessfulMutation_ReturnsResultWithoutTimeoutFeedback()
    {
        var result = new RoleMenuEditResult(RoleMenuEditStatus.Updated, "Updated");
        var feedbackCalls = 0;
        var observed = await RoleMenuEditMutationWaiter.WaitAsync(
            Task.FromResult(result), () =>
            {
                feedbackCalls++;
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Same(result, observed);
        Assert.Equal(0, feedbackCalls);
    }
}
