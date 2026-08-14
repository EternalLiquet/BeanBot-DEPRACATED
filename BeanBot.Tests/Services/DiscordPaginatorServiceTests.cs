using BeanBot.Services;

using Discord;

using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace BeanBot.Tests.Services;

public class DiscordPaginatorServiceTests
{
    [Fact]
    public void Cursor_NavigatesWithinBounds()
    {
        var cursor = new PaginationCursor(3);

        Assert.False(cursor.Move(PaginationAction.Previous));
        Assert.True(cursor.Move(PaginationAction.Next));
        Assert.Equal(1, cursor.PageIndex);
        Assert.True(cursor.Move(PaginationAction.Last));
        Assert.Equal(2, cursor.PageIndex);
        Assert.False(cursor.Move(PaginationAction.Next));
        Assert.True(cursor.Move(PaginationAction.First));
        Assert.Equal(0, cursor.PageIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Cursor_RejectsInvalidPageCount(int pageCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaginationCursor(pageCount));
    }

    [Fact]
    public void Cursor_NonNavigationActions_DoNotMove()
    {
        var cursor = new PaginationCursor(2);

        Assert.False(cursor.Move(PaginationAction.None));
        Assert.False(cursor.Move(PaginationAction.Stop));
        Assert.Equal(0, cursor.PageIndex);
    }

    [Fact]
    public async Task SessionLifetime_StopCancelsAndObservesExpirationBeforeReleasingSlot()
    {
        using var lifetime = new PaginatorSessionLifetime(CancellationToken.None);
        using var availableSlots = new SemaphoreSlim(0, 1);
        lifetime.ExpirationTask = WaitForCancellationAsync(lifetime.ExpirationCancellation);

        Assert.True(lifetime.TryBeginCompletion());
        lifetime.CancelExpiration();
        await lifetime.ExpirationTask;
        Assert.False(availableSlots.Wait(0));

        lifetime.ReleaseSlot(availableSlots);
        Assert.True(availableSlots.Wait(0));
    }

    [Fact]
    public async Task SessionLifetime_ShutdownCancelsExpiration()
    {
        using var shutdown = new CancellationTokenSource();
        using var lifetime = new PaginatorSessionLifetime(shutdown.Token);
        lifetime.ExpirationTask = WaitForCancellationAsync(lifetime.ExpirationCancellation);

        shutdown.Cancel();

        await lifetime.ExpirationTask;
        Assert.True(lifetime.ExpirationCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task SessionLifetime_ExpirationRetainsSlotUntilCleanupCompletes()
    {
        using var lifetime = new PaginatorSessionLifetime(CancellationToken.None);
        using var availableSlots = new SemaphoreSlim(0, 1);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanupToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(lifetime.TryBeginCompletion());
        lifetime.ExpirationTask = CompleteCleanupAsync();
        await cleanupStarted.Task;
        Assert.False(availableSlots.Wait(0));

        allowCleanupToComplete.SetResult();
        await lifetime.ExpirationTask;
        Assert.True(availableSlots.Wait(0));

        async Task CompleteCleanupAsync()
        {
            cleanupStarted.SetResult();
            await allowCleanupToComplete.Task;
            lifetime.ReleaseSlot(availableSlots);
        }
    }

    [Fact]
    public async Task SessionLifetime_DisposalWaitsForInFlightCleanupSignal()
    {
        using var lifetime = new PaginatorSessionLifetime(CancellationToken.None);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanupToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(lifetime.TryBeginCompletion());
        var cleanup = CompleteCleanupAsync();
        await cleanupStarted.Task;
        var disposalWait = lifetime.CompletionTask;
        Assert.False(disposalWait.IsCompleted);

        allowCleanupToComplete.SetResult();
        await cleanup;
        await disposalWait;

        async Task CompleteCleanupAsync()
        {
            cleanupStarted.SetResult();
            await allowCleanupToComplete.Task;
            lifetime.MarkCompletionFinished();
        }
    }

    [Fact]
    public async Task SessionLifetime_LosingStopWaitsForWinningCleanup()
    {
        using var lifetime = new PaginatorSessionLifetime(CancellationToken.None);
        Assert.True(lifetime.TryBeginCompletion());

        var losingStop = WaitAsLosingStopAsync();
        Assert.False(losingStop.IsCompleted);

        lifetime.MarkCompletionFinished();
        await losingStop;

        async Task WaitAsLosingStopAsync()
        {
            if (!lifetime.TryBeginCompletion())
            {
                await lifetime.CompletionTask;
            }
        }
    }

    [Fact]
    public async Task SessionLifetime_LosingStopReleasesAccessBeforeWaitingForCleanup()
    {
        using var lifetime = new PaginatorSessionLifetime(CancellationToken.None);
        using var access = new SemaphoreSlim(1, 1);
        Assert.True(lifetime.TryBeginCompletion());
        await access.WaitAsync();

        var losingStop = WaitAsLosingStopAsync();
        await access.WaitAsync().WaitAsync(TimeSpan.FromSeconds(1));
        lifetime.MarkCompletionFinished();
        access.Release();

        await losingStop.WaitAsync(TimeSpan.FromSeconds(1));

        async Task WaitAsLosingStopAsync()
        {
            access.Release();
            if (!lifetime.TryBeginCompletion())
            {
                await lifetime.CompletionTask;
            }
        }
    }

    [Fact]
    public async Task ReactionRemoval_UsesUserIdForRepeatedControlsWithoutCachedUser()
    {
        var message = DispatchProxy.Create<IUserMessage, ReactionRemovalMessageProxy>();
        var recorder = (ReactionRemovalMessageProxy)message;
        var control = new Emoji("▶");

        await DiscordPaginatorService.TryRemoveUserReactionAsync(message, control, 42);
        await DiscordPaginatorService.TryRemoveUserReactionAsync(message, control, 42);

        Assert.Equal(new ulong[] { 42, 42 }, recorder.RemovedUserIds);
        Assert.All(recorder.RemovedEmotes, emote => Assert.Equal(control, emote));
    }

    [Fact]
    public async Task SessionLifetime_RepeatedStopAndSlotReuse_RemainsBounded()
    {
        using var availableSlots = new SemaphoreSlim(1, 1);

        for (var iteration = 0; iteration < 256; iteration++)
        {
            Assert.True(availableSlots.Wait(0));
            using var lifetime = new PaginatorSessionLifetime(CancellationToken.None);
            lifetime.ExpirationTask = WaitForCancellationAsync(lifetime.ExpirationCancellation);

            Assert.True(lifetime.TryBeginCompletion());
            Assert.False(lifetime.TryBeginCompletion());
            lifetime.CancelExpiration();
            await lifetime.ExpirationTask;
            lifetime.ReleaseSlot(availableSlots);
            lifetime.ReleaseSlot(availableSlots);
        }

        Assert.Equal(1, availableSlots.CurrentCount);
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public class ReactionRemovalMessageProxy : DispatchProxy
    {
        public List<ulong> RemovedUserIds { get; } = new();
        public List<IEmote> RemovedEmotes { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IMessage.RemoveReactionAsync)
                && args is [IEmote emote, ulong userId, ..])
            {
                RemovedEmotes.Add(emote);
                RemovedUserIds.Add(userId);
                return Task.CompletedTask;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
