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
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        var desiredStates = new List<bool>();
        var concurrentMutations = 0;
        var maximumConcurrentMutations = 0;

        async Task Mutate(bool desiredState, CancellationToken _)
        {
            var current = Interlocked.Increment(ref concurrentMutations);
            maximumConcurrentMutations = Math.Max(maximumConcurrentMutations, current);
            desiredStates.Add(desiredState);
            try
            {
                if (desiredStates.Count == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
            }
            finally
            {
                Interlocked.Decrement(ref concurrentMutations);
            }
        }

        var owner = coordinator.Submit(key, 42, desiredState: true, Mutate, CancellationToken.None);
        Assert.NotNull(owner);
        await firstStarted.Task;

        Assert.Null(coordinator.Submit(key, 42, desiredState: false, Mutate, CancellationToken.None));
        releaseFirst.SetResult();
        await owner!;

        Assert.Equal(new[] { true, false }, desiredStates);
        Assert.Equal(1, maximumConcurrentMutations);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Fact]
    public async Task Submit_RemoveThenAddWhileRemoveIsRunning_ConvergesToPresent()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
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
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
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

        Assert.Null(coordinator.Submit(key, 42, desiredState: false, Mutate, CancellationToken.None));
        Assert.Null(coordinator.Submit(key, 42, desiredState: true, Mutate, CancellationToken.None));
        releaseFirst.SetResult();
        await owner!;

        Assert.Equal(new[] { true }, desiredStates);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Fact]
    public async Task Submit_DuplicateSameDirectionEventsShareOneMutation()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        var calls = 0;

        async Task Mutate(bool _, CancellationToken __)
        {
            calls++;
            firstStarted.TrySetResult();
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
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Fact]
    public async Task Submit_DifferentUsersCanMutateSameRoleConcurrently()
    {
        var coordinator = CreateCoordinator();
        var firstStarted = NewCompletion();
        var secondStarted = NewCompletion();
        var release = NewCompletion();

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
        var firstStarted = NewCompletion();
        var secondStarted = NewCompletion();
        var release = NewCompletion();

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
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Submit_LateMutationCannotOvertakeNewerOppositeState(bool firstState)
    {
        var coordinator = CreateCoordinator(capacity: 1, timeout: TimeSpan.FromMilliseconds(25));
        var key = new ReactionRoleMutationKey(1, 2, 3);
        var release = NewCompletion();
        var started = NewCompletion();
        var states = new List<bool>();
        var finalState = !firstState;
        async Task Mutate(bool desired, CancellationToken _)
        {
            states.Add(desired);
            if (states.Count == 1)
            {
                started.TrySetResult();
                await release.Task;
            }
            finalState = desired;
        }
        var owner = coordinator.Submit(key, 42, firstState, Mutate, CancellationToken.None)!;
        await started.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => owner);
        Assert.Equal(1, coordinator.ActiveKeyCount);
        Assert.Null(coordinator.Submit(key, 43, !firstState, Mutate, CancellationToken.None));
        Assert.Null(coordinator.Submit(new ReactionRoleMutationKey(1, 9, 3), 43, true,
            (_, _) => throw new InvalidOperationException("capacity bypass"), CancellationToken.None));
        Assert.Single(states);
        var rawWorkers = coordinator.SnapshotOperations();

        release.SetResult();
        await Task.WhenAll(rawWorkers).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { firstState, !firstState }, states);
        Assert.Equal(!firstState, finalState);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Submit_FailedRawAttemptFollowsNewerEventIncludingSameDirection(bool newerState)
    {
        var coordinator = CreateCoordinator(timeout: TimeSpan.FromMilliseconds(25));
        var key = new ReactionRoleMutationKey(1, 2, 3);
        var first = NewCompletion();
        var started = NewCompletion();
        var calls = new List<bool>();
        var owner = coordinator.Submit(key, 42, true, async (state, _) =>
        {
            calls.Add(state);
            started.SetResult();
            await first.Task;
        }, CancellationToken.None)!;
        await started.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => owner);
        Assert.Null(coordinator.Submit(key, 43, newerState, (state, _) =>
        {
            calls.Add(state);
            return Task.CompletedTask;
        }, CancellationToken.None));
        var rawWorkers = coordinator.SnapshotOperations();

        first.SetException(new InvalidOperationException("late REST failure"));
        await Task.WhenAll(rawWorkers).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { true, newerState }, calls);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Fact]
    public async Task Submit_LateFailureWithoutNewEventDoesNotRetry()
    {
        var coordinator = CreateCoordinator(timeout: TimeSpan.FromMilliseconds(25));
        var first = NewCompletion();
        var calls = 0;
        var owner = coordinator.Submit(new ReactionRoleMutationKey(1, 2, 3), 42, true, (_, _) =>
        {
            calls++;
            return first.Task;
        }, CancellationToken.None)!;
        await Assert.ThrowsAsync<TimeoutException>(() => owner);
        Assert.Equal(1, coordinator.ActiveKeyCount);
        var rawWorkers = coordinator.SnapshotOperations();
        first.SetException(new InvalidOperationException("late REST failure"));
        await Task.WhenAll(rawWorkers);
        Assert.Equal(1, calls);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Fact]
    public async Task Submit_ShutdownCancellationReleasesKeyAndRejectsNewWork()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);
        using var stopping = new CancellationTokenSource();
        var started = NewCompletion();

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

        var rawWorkers = coordinator.SnapshotOperations();
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(rawWorkers));
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
    public async Task Stop_CanceledCallerRetainsUncooperativeWorkerAndSuppressesFollowup()
    {
        var coordinator = CreateCoordinator();
        var key = new ReactionRoleMutationKey(1, 2, 3);
        using var cancellation = new CancellationTokenSource();
        var first = NewCompletion();
        var started = NewCompletion();
        var followupCalls = 0;
        var owner = coordinator.Submit(key, 42, true, async (_, _) =>
        {
            started.SetResult();
            await first.Task;
        }, cancellation.Token)!;
        await started.Task;
        Assert.Null(coordinator.Submit(key, 43, false, (_, _) =>
        {
            followupCalls++;
            return Task.CompletedTask;
        }, cancellation.Token));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        var drain = coordinator.StopAsync();
        Assert.False(drain.IsCompleted);
        Assert.Equal(1, coordinator.ActiveKeyCount);
        Assert.Null(coordinator.Submit(key, 44, true, (_, _) => Task.CompletedTask, CancellationToken.None));
        first.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, followupCalls);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    private static ReactionRoleMutationCoordinator CreateCoordinator(int capacity = 16, TimeSpan? timeout = null)
        => new(capacity, NullLogger.Instance, timeout);

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
