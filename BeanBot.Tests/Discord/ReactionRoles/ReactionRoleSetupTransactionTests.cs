using BeanBot.Discord.ReactionRoles;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class ReactionRoleSetupTransactionTests
{
    [Theory]
    [InlineData((int)ReactionRoleAssignabilityStatus.RoleMissing)]
    [InlineData((int)ReactionRoleAssignabilityStatus.EveryoneRole)]
    [InlineData((int)ReactionRoleAssignabilityStatus.ManagedRole)]
    [InlineData((int)ReactionRoleAssignabilityStatus.BotMissingManageRoles)]
    [InlineData((int)ReactionRoleAssignabilityStatus.BotHierarchyTooLow)]
    [InlineData((int)ReactionRoleAssignabilityStatus.InvokerHierarchyTooLow)]
    public async Task ExecuteIfAssignableAsync_RejectedTargetNeverPublishesOrPersists(int rejectedStatus)
    {
        var published = 0;
        var persisted = 0;
        var deleted = 0;
        var status = await ReactionRoleSetupTransaction.ExecuteIfAssignableAsync(
            () => (ReactionRoleAssignabilityStatus)rejectedStatus,
            () =>
            {
                published++;
                return Task.FromResult("panel");
            },
            _ =>
            {
                persisted++;
                return Task.CompletedTask;
            },
            _ =>
            {
                deleted++;
                return Task.CompletedTask;
            },
            _ => throw new InvalidOperationException("No compensation should be needed."));

        Assert.Equal((ReactionRoleAssignabilityStatus)rejectedStatus, status);
        Assert.Equal(0, published);
        Assert.Equal(0, persisted);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task ExecuteIfAssignableAsync_AllowedTargetPublishesBeforePersisting()
    {
        var operations = new List<string>();
        var status = await ReactionRoleSetupTransaction.ExecuteIfAssignableAsync(
            () => ReactionRoleAssignabilityStatus.Allowed,
            () =>
            {
                operations.Add("publish");
                return Task.FromResult("panel");
            },
            _ =>
            {
                operations.Add("persist");
                return Task.CompletedTask;
            },
            _ => throw new InvalidOperationException("No rollback should be needed."),
            _ => throw new InvalidOperationException("No compensation should be needed."));

        Assert.Equal(ReactionRoleAssignabilityStatus.Allowed, status);
        Assert.Equal(new[] { "publish", "persist" }, operations);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessDoesNotDeleteMessage()
    {
        var deleted = false;

        var result = await ReactionRoleSetupTransaction.ExecuteAsync(
            () => Task.FromResult("message"),
            _ => Task.CompletedTask,
            _ =>
            {
                deleted = true;
                return Task.CompletedTask;
            },
            _ => { });

        Assert.Equal("message", result);
        Assert.False(deleted);
    }

    [Theory]
    [InlineData("reaction")]
    [InlineData("persistence")]
    public async Task ExecuteAsync_PostCreationFailureDeletesIncompleteMessage(string failureStage)
    {
        var expected = new InvalidOperationException(failureStage);
        var deleted = false;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReactionRoleSetupTransaction.ExecuteAsync(
                () => Task.FromResult("message"),
                _ => Task.FromException(expected),
                _ =>
                {
                    deleted = true;
                    return Task.CompletedTask;
                },
                _ => { }));

        Assert.Same(expected, actual);
        Assert.True(deleted);
    }

    [Fact]
    public async Task ExecuteAsync_CompensationFailureDoesNotHideOriginalFailure()
    {
        var original = new InvalidOperationException("persistence failed");
        var compensation = new InvalidOperationException("delete failed");
        Exception? reportedCompensation = null;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReactionRoleSetupTransaction.ExecuteAsync(
                () => Task.FromResult("message"),
                _ => Task.FromException(original),
                _ => Task.FromException(compensation),
                exception => reportedCompensation = exception));

        Assert.Same(original, actual);
        Assert.Same(compensation, reportedCompensation);
    }

    [Fact]
    public async Task ExecuteAsync_CreateFailureHasNothingToCompensate()
    {
        var deleted = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReactionRoleSetupTransaction.ExecuteAsync<string>(
                () => Task.FromException<string>(new InvalidOperationException("create failed")),
                _ => Task.CompletedTask,
                _ =>
                {
                    deleted = true;
                    return Task.CompletedTask;
                },
                _ => { }));

        Assert.False(deleted);
    }
}
