using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuSelectionReconcilerTests
{
    [Fact]
    public void Create_CommitThenThrowAddAndRemove_ReportsActualCompletedState()
    {
        var result = RoleMenuSelectionReconciler.Create(
            [1UL, 2UL],
            [2UL],
            [1UL, 99UL],
            [2UL, 99UL]);

        Assert.Equal([2UL], result.AddedRoleIds);
        Assert.Equal([1UL], result.RemovedRoleIds);
        Assert.Empty(result.MissingSelectedRoleIds);
        Assert.Empty(result.StillAssignedUnselectedRoleIds);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Create_UnappliedOperations_ReportCurrentMismatch()
    {
        var result = RoleMenuSelectionReconciler.Create(
            [1UL, 2UL],
            [2UL],
            [1UL, 99UL],
            [1UL, 99UL]);

        Assert.Empty(result.AddedRoleIds);
        Assert.Empty(result.RemovedRoleIds);
        Assert.Equal([2UL], result.MissingSelectedRoleIds);
        Assert.Equal([1UL], result.StillAssignedUnselectedRoleIds);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void Create_IgnoresUnconfiguredRoleChanges()
    {
        var result = RoleMenuSelectionReconciler.Create(
            [1UL],
            [1UL],
            [99UL],
            [1UL, 100UL]);

        Assert.Equal([1UL], result.AddedRoleIds);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void FormatReconciliation_MaximumRoleNames_StaysWithinDiscordContentLimit()
    {
        var roleIds = Enumerable.Range(1, 25).Select(value => (ulong)value).ToList();
        var reconciliation = new RoleMenuSelectionReconciliation(
            roleIds,
            [],
            [],
            roleIds);
        var names = roleIds.ToDictionary(
            roleId => roleId,
            roleId => new string((char)('A' + roleId % 26), 100));

        var content = RoleMenuMemberModule.FormatReconciliation(reconciliation, names);

        Assert.True(content.Length <= RoleMenuConstants.MaximumResponseContentLength);
        Assert.Contains("Some role details were omitted", content, StringComparison.Ordinal);
    }
}
