using BeanBot.Discord.ReactionRoles;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class ReactionRoleSettingsLifecycleCoordinatorTests
{
    [Fact]
    public async Task WriterWaitsForAdmittedReaderAndBlocksNewReader()
    {
        var coordinator = new ReactionRoleSettingsLifecycleCoordinator();
        var firstReaderEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReader = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReaderEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstReader = coordinator.RunReadAsync(
            123UL,
            async cancellationToken =>
            {
                firstReaderEntered.TrySetResult();
                await releaseFirstReader.Task.WaitAsync(cancellationToken);
            },
            CancellationToken.None);
        await firstReaderEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var writer = coordinator.RunWriteAsync(
            123UL,
            async cancellationToken =>
            {
                writerEntered.TrySetResult();
                await releaseWriter.Task.WaitAsync(cancellationToken);
                return true;
            },
            CancellationToken.None);
        var secondReader = coordinator.RunReadAsync(
            123UL,
            _ =>
            {
                secondReaderEntered.TrySetResult();
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(writerEntered.Task.IsCompleted);
        Assert.False(secondReaderEntered.Task.IsCompleted);
        releaseFirstReader.TrySetResult();
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(secondReaderEntered.Task.IsCompleted);

        releaseWriter.TrySetResult();
        Assert.True(await writer);
        await Task.WhenAll(firstReader, secondReader);
        Assert.True(secondReaderEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task DifferentStripesCanProceedConcurrently()
    {
        var coordinator = new ReactionRoleSettingsLifecycleCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunWriteAsync(
            1UL,
            async cancellationToken =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return true;
            },
            CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var second = coordinator.RunWriteAsync(
            2UL,
            _ =>
            {
                secondEntered.TrySetResult();
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(secondEntered.Task.IsCompleted);
        releaseFirst.TrySetResult();
        Assert.True(await first);
    }
}
