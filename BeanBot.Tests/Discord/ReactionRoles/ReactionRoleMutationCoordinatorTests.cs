using BeanBot.Discord.ReactionRoles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class ReactionRoleMutationCoordinatorTests
{
    [Fact]
    public async Task Submit_AddThenRemoveWhileAddIsRunning_ConvergesToAbsent()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var desiredStates = new List<bool>();

        async Task Mutate(bool desiredState, CancellationToken _)
        {
            desiredStates.Add(desiredState);
            if (desiredStates.Count == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
        }

        var owner = coordinator.Submit(key, 42, desiredState: true, Mutate, CancellationToken.None);
        Assert.NotNull(owner);
        await firstStarted.Task;

        var coalesced = coordinator.Submit(key, 42, desiredState: false, Mutate, CancellationToken.None);
        Assert.Null(coalesced);
        releaseFirst.SetResult();
        await owner!;

        Assert.Equal(new[] { true, false }, desiredStates);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Fact]
    public async Task Submit_RemoveThenAddWhileRemoveIsRunning_ConvergesToPresent()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var desiredStates = new List<bool>();

        async Task Mutate(bool desiredState, CancellationToken _)
        {
            desiredStates.Add(desiredState);
            if (desiredStates.Count == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
        }

        var owner = coordinator.Submit(key, 42, desiredState: false, Mutate, CancellationToken.None);
        Assert.NotNull(owner);
        await firstStarted.Task;

        Assert.Null(coordinator.Submit(key, 42, desiredState: true, Mutate, CancellationToken.None));
        releaseFirst.SetResult();
        await owner!;

        Assert.Equal(new[] { false, true }, desiredStates);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Fact]
    public async Task Submit_RapidTogglesEndingAtActiveState_DoNotQueueRedundantMutation()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task Mutate(bool desiredState, CancellationToken _)
        {
            Assert.True(desiredState);
            calls++;
            firstStarted.SetResult();
            await releaseFirst.Task;
        }

        var owner = coordinator.Submit(key, 42, desiredState: true, Mutate, CancellationToken.None);
        Assert.NotNull(owner);
        await firstStarted.Task;

        Assert.Null(coordinator.Submit(key, 42, desiredState: false, Mutate, CancellationToken.None));
        Assert.Null(coordinator.Submit(key, 42, desiredState: true, Mutate, CancellationToken.None));
        releaseFirst.SetResult();
        await owner!;

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Submit_DuplicateSameDirectionEventsShareOneMutation()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task Mutate(bool _, CancellationToken __)
        {
            calls++;
            firstStarted.SetResult();
            await releaseFirst.Task;
        }

        var owner = coordinator.Submit(key, 42, desiredState: true, Mutate, CancellationToken.None);
        Assert.NotNull(owner);
        await firstStarted.Task;

        for (var index = 0; index < 100; index++)
        {
            Assert.Null(coordinator.Submit(key, 42, desiredState: true, Mutate, CancellationToken.None));
        }

        Assert.Equal(1, coordinator.ActiveKeyCount);
        releaseFirst.SetResult();
        await owner!;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Submit_DifferentUsersCanMutateSameRoleConcurrently()
    {
        var coordinator = CreateCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task First(bool _, CancellationToken __)
        {
            firstStarted.SetResult();
            await release.Task;
        }

        async Task Second(bool _, CancellationToken __)
        {
            secondStarted.SetResult();
            await release.Task;
        }

        var first = coordinator.Submit(new ReactionRoleMutationKey(1, 10, 99), 42, true, First, CancellationToken.None);
        var second = coordinator.Submit(new ReactionRoleMutationKey(1, 11, 99), 42, true, Second, CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);

        await Task.WhenAll(firstStarted.Task, secondStarted.Task);
        Assert.Equal(2, coordinator.ActiveKeyCount);
        release.SetResult();
        await Task.WhenAll(first!, second!);
    }

    [Fact]
    public async Task Submit_SameUserCanMutateDifferentRolesConcurrently()
    {
        var coordinator = CreateCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task First(bool _, CancellationToken __)
        {
            firstStarted.SetResult();
            await release.Task;
        }

        async Task Second(bool _, CancellationToken __)
        {
            secondStarted.SetResult();
            await release.Task;
        }

        var first = coordinator.Submit(new ReactionRoleMutationKey(1, 10, 98), 42, true, First, CancellationToken.None);
        var second = coordinator.Submit(new ReactionRoleMutationKey(1, 10, 99), 42, true, Second, CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);

        await Task.WhenAll(firstStarted.Task, secondStarted.Task);
        Assert.Equal(2, coordinator.ActiveKeyCount);
        release.SetResult();
        await Task.WhenAll(first!, second!);
    }

    [Fact]
    public async Task Submit_CapacityExhaustionDoesNotGrowStateOrStartExtraMutation()
    {
        var coordinator = CreateCoordinator(capacity: 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = false;

        async Task First(bool _, CancellationToken __)
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
        }

        var owner = coordinator.Submit(new ReactionRoleMutationKey(1, 10, 98), 42, true, First, CancellationToken.None);
        Assert.NotNull(owner);
        await firstStarted.Task;

        var rejected = coordinator.Submit(
            new ReactionRoleMutationKey(1, 11, 99),
            43,
            true,
            (_, _) =>
            {
                secondStarted = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Null(rejected);
        Assert.False(secondStarted);
        Assert.Equal(1, coordinator.ActiveKeyCount);
        releaseFirst.SetResult();
        await owner!;
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Fact]
    public async Task Submit_FailedMutationReleasesKeyForFutureEvent()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);

        var failed = coordinator.Submit(
            key,
            42,
            true,
            (_, _) => Task.FromException(new InvalidOperationException("Discord failed")),
            CancellationToken.None);
        Assert.NotNull(failed);
        await failed!;
        Assert.Equal(0, coordinator.ActiveKeyCount);

        var retried = false;
        var retry = coordinator.Submit(
            key,
            42,
            false,
            (_, _) =>
            {
                retried = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        Assert.NotNull(retry);
        await retry!;

        Assert.True(retried);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Fact]
    public async Task Submit_ShutdownCancellationReleasesKeyAndRejectsNewWork()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);
        using var stopping = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var owner = coordinator.Submit(
            key,
            42,
            true,
            async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            stopping.Token);
        Assert.NotNull(owner);
        await started.Task;

        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner!);
        Assert.Equal(0, coordinator.ActiveKeyCount);

        var startedAfterShutdown = false;
        Assert.Null(coordinator.Submit(
            key,
            42,
            false,
            (_, _) =>
            {
                startedAfterShutdown = true;
                return Task.CompletedTask;
            },
            stopping.Token));
        Assert.False(startedAfterShutdown);
    }

    [Fact]
    public async Task RunBoundedAsync_TimeoutDoesNotWaitForUnderlyingTaskToFinish()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            ReactionRoleMutationCoordinator.RunBoundedAsync(
                _ => operation.Task,
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None));

        operation.SetException(new InvalidOperationException("late failure"));
        await Task.Yield();
    }

    [Fact]
    public async Task RunBoundedAsync_ShutdownCancellationIsNotReportedAsTimeout()
    {
        using var stopping = new CancellationTokenSource();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = ReactionRoleMutationCoordinator.RunBoundedAsync(
            async operationCancellation =>
            {
                operationStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, operationCancellation);
            },
            TimeSpan.FromSeconds(1),
            stopping.Token);
        await operationStarted.Task;

        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static ReactionRoleMutationCoordinator CreateCoordinator(int capacity = 16)
        => new(capacity, NullLogger.Instance);
}
