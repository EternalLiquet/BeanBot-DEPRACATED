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

    [Fact]
    public async Task ValueReturningReader_SharesLeaseAndReturnsItsResult()
    {
        var coordinator = new ReactionRoleSettingsLifecycleCoordinator();
        var readerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReader = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = coordinator.RunReadAsync(123UL, async token =>
        {
            readerEntered.TrySetResult();
            await releaseReader.Task.WaitAsync(token);
            return 42;
        }, CancellationToken.None);
        await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var writer = coordinator.RunWriteAsync(123UL,
            _ => Task.FromResult(true), CancellationToken.None);
        Assert.False(writer.IsCompleted);
        releaseReader.TrySetResult();
        Assert.Equal(42, await reader);
        Assert.True(await writer.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancelledQueuedWriter_ReleasesTurnstileForNextReader()
    {
        var coordinator = new ReactionRoleSettingsLifecycleCoordinator();
        var readerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReader = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = coordinator.RunReadAsync(123UL, async token =>
        {
            readerEntered.TrySetResult();
            await releaseReader.Task.WaitAsync(token);
        }, CancellationToken.None);
        await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        using var cancellation = new CancellationTokenSource();
        var writer = coordinator.RunWriteAsync(123UL,
            _ => Task.FromResult(true), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer);

        var nextReader = coordinator.RunReadAsync(123UL,
            _ => Task.FromResult(42), CancellationToken.None);
        Assert.Equal(42, await nextReader.WaitAsync(TimeSpan.FromSeconds(1)));
        releaseReader.TrySetResult();
        await reader;
    }

    [Fact]
    public async Task CancelledQueuedReader_DoesNotAcquireWriterLease()
    {
        var coordinator = new ReactionRoleSettingsLifecycleCoordinator();
        var writerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = coordinator.RunWriteAsync(123UL, async token =>
        {
            writerEntered.TrySetResult();
            await releaseWriter.Task.WaitAsync(token);
            return true;
        }, CancellationToken.None);
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        using var cancellation = new CancellationTokenSource();
        var reader = coordinator.RunReadAsync(123UL,
            _ => Task.FromResult(42), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);

        releaseWriter.TrySetResult();
        Assert.True(await writer);
        Assert.Equal(43, await coordinator.RunReadAsync(123UL,
            _ => Task.FromResult(43), CancellationToken.None));
    }
}
