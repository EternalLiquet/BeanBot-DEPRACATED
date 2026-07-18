using BeanBot.Services;
using Xunit;

namespace BeanBot.Tests.Services;

public class ReactionRoleSetupTransactionTests
{
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
