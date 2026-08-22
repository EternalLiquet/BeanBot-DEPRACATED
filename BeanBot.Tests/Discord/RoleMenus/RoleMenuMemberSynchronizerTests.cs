using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuMemberSynchronizerTests
{
    [Fact]
    public async Task SynchronizeAsync_AddsBeforeRemovingForSingleSelection()
    {
        var mutator = new RecordingMutator();
        var plan = new RoleMenuSelectionPlan([2UL], [2UL], [1UL]);

        var result = await new RoleMenuMemberSynchronizer().SynchronizeAsync(
            plan,
            RoleMenuSelectionMode.Single,
            mutator,
            CancellationToken.None);

        Assert.Equal(["add:2", "remove:1"], mutator.Calls);
        Assert.Equal([2UL], result.AddedRoleIds);
        Assert.Equal([1UL], result.RemovedRoleIds);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task SynchronizeAsync_FailedSingleReplacement_KeepsExistingRole()
    {
        var expected = new InvalidOperationException("missing permissions");
        var mutator = new RecordingMutator
        {
            Add = _ => Task.FromException(expected)
        };
        var plan = new RoleMenuSelectionPlan([2UL], [2UL], [1UL]);

        var result = await new RoleMenuMemberSynchronizer().SynchronizeAsync(
            plan,
            RoleMenuSelectionMode.Single,
            mutator,
            CancellationToken.None);

        Assert.Equal(["add:2"], mutator.Calls);
        var failure = Assert.Single(result.Failures);
        Assert.Same(expected, failure.Exception);
        Assert.Equal([1UL], result.SkippedRemovalRoleIds);
    }

    [Fact]
    public async Task SynchronizeAsync_PartialFailures_ReportActualCompletedChanges()
    {
        var mutator = new RecordingMutator
        {
            Add = roleId => roleId == 2
                ? Task.FromException(new InvalidOperationException("add failed"))
                : Task.CompletedTask,
            Remove = roleId => roleId == 4
                ? Task.FromException(new InvalidOperationException("remove failed"))
                : Task.CompletedTask
        };
        var plan = new RoleMenuSelectionPlan(
            [1UL, 2UL],
            [1UL, 2UL],
            [3UL, 4UL]);

        var result = await new RoleMenuMemberSynchronizer().SynchronizeAsync(
            plan,
            RoleMenuSelectionMode.Multiple,
            mutator,
            CancellationToken.None);

        Assert.Equal([1UL], result.AddedRoleIds);
        Assert.Equal([3UL], result.RemovedRoleIds);
        Assert.Equal([2UL, 4UL], result.Failures.Select(failure => failure.RoleId));
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task SynchronizeAsync_CancellationReportsCompletedAndUnattemptedMutations()
    {
        using var cancellation = new CancellationTokenSource();
        var mutator = new RecordingMutator
        {
            Add = _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        var plan = new RoleMenuSelectionPlan([1UL, 2UL], [1UL, 2UL], []);

        var result = await new RoleMenuMemberSynchronizer().SynchronizeAsync(
            plan,
            RoleMenuSelectionMode.Multiple,
            mutator,
            cancellation.Token);

        Assert.Equal(["add:1"], mutator.Calls);
        Assert.Equal([1UL], result.AddedRoleIds);
        var interruption = Assert.IsType<RoleMenuMutationInterruption>(result.Interruption);
        Assert.Equal(2UL, interruption.RoleId);
        Assert.Equal(RoleMenuMutationInterruptionKind.NotAttempted, interruption.Kind);
    }

    [Fact]
    public async Task SynchronizeAsync_CancelledApiCallReportsUnknownOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var mutator = new RecordingMutator
        {
            Add = _ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            }
        };
        var plan = new RoleMenuSelectionPlan([1UL], [1UL], []);

        var result = await new RoleMenuMemberSynchronizer().SynchronizeAsync(
            plan,
            RoleMenuSelectionMode.Multiple,
            mutator,
            cancellation.Token);

        Assert.Empty(result.AddedRoleIds);
        var interruption = Assert.IsType<RoleMenuMutationInterruption>(result.Interruption);
        Assert.Equal(1UL, interruption.RoleId);
        Assert.Equal(RoleMenuMutationInterruptionKind.OutcomeUnknown, interruption.Kind);
    }

    private sealed class RecordingMutator : IRoleMenuMemberMutator
    {
        public List<string> Calls { get; } = [];
        public Func<ulong, Task> Add { get; init; } = _ => Task.CompletedTask;
        public Func<ulong, Task> Remove { get; init; } = _ => Task.CompletedTask;

        public async Task AddRoleAsync(ulong roleId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"add:{roleId}");
            await Add(roleId);
        }

        public async Task RemoveRoleAsync(ulong roleId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"remove:{roleId}");
            await Remove(roleId);
        }
    }
}
