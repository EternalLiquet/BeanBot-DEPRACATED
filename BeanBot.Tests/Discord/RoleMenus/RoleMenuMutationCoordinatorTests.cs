using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuMutationCoordinatorTests
{
    [Fact]
    public async Task RunMenuWriteAsync_SerializesPublishAndDeleteForStableMenuId()
    {
        var coordinator = new RoleMenuMutationCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;
        var first = coordinator.RunMenuWriteAsync(
            "menu",
            async _ =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
                return 1;
            },
            CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = coordinator.RunMenuWriteAsync(
            "menu",
            _ =>
            {
                secondEntered = true;
                return Task.FromResult(2);
            },
            CancellationToken.None);

        await Task.Yield();
        Assert.False(secondEntered);
        releaseFirst.SetResult();

        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.True(secondEntered);
    }

    [Fact]
    public async Task RunAsync_CancelledWaiterNeverRunsOperation()
    {
        var coordinator = new RoleMenuMutationCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.RunMenuWriteAsync(
            "menu",
            async _ =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
                return 1;
            },
            CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var invoked = false;
        var second = coordinator.RunMenuWriteAsync(
            "menu",
            _ =>
            {
                invoked = true;
                return Task.FromResult(2);
            },
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(invoked);
        releaseFirst.SetResult();
        await first;
    }

    [Fact]
    public async Task RunAsync_SerializesSameMemberAcrossDifferentMenus()
    {
        var coordinator = new RoleMenuMutationCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;
        var first = coordinator.RunMemberAsync(
            "menu:first",
            "member:guild:user",
            async _ =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
                return 1;
            },
            CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = coordinator.RunMemberAsync(
            "menu:second",
            "member:guild:user",
            _ =>
            {
                secondEntered = true;
                return Task.FromResult(2);
            },
            CancellationToken.None);

        await Task.Yield();
        Assert.False(secondEntered);
        releaseFirst.SetResult();

        await Task.WhenAll(first, second);
        Assert.True(secondEntered);
    }

    [Fact]
    public async Task RunMemberAsync_AllowsDifferentMembersOnSameMenuConcurrently()
    {
        var coordinator = new RoleMenuMutationCoordinator();
        const string firstMember = "member:first";
        var secondMember = Enumerable.Range(1, 1000)
            .Select(value => $"member:{value}")
            .First(candidate => RoleMenuMutationCoordinator.GetStripeIndex(candidate)
                                != RoleMenuMutationCoordinator.GetStripeIndex(firstMember));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.RunMemberAsync(
            "menu",
            firstMember,
            async _ =>
            {
                firstEntered.SetResult();
                await release.Task;
                return 1;
            },
            CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = coordinator.RunMemberAsync(
            "menu",
            secondMember,
            async _ =>
            {
                secondEntered.SetResult();
                await release.Task;
                return 2;
            },
            CancellationToken.None);

        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        release.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task RunMenuWriteAsync_WaitsForActiveMemberMutation()
    {
        var coordinator = new RoleMenuMutationCoordinator();
        var memberEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMember = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteEntered = false;
        var member = coordinator.RunMemberAsync(
            "menu",
            "member",
            async _ =>
            {
                memberEntered.SetResult();
                await releaseMember.Task;
                return 1;
            },
            CancellationToken.None);
        await memberEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var delete = coordinator.RunMenuWriteAsync(
            "menu",
            _ =>
            {
                deleteEntered = true;
                return Task.FromResult(2);
            },
            CancellationToken.None);

        await Task.Yield();
        Assert.False(deleteEntered);
        releaseMember.SetResult();

        await Task.WhenAll(member, delete);
        Assert.True(deleteEntered);
    }
}
