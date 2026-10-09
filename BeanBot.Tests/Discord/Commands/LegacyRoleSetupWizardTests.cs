using BeanBot.Discord.Commands;
using BeanBot.Discord.Messaging;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class LegacyRoleSetupWizardTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task CancelAtEveryStage_StopsBeforePublication(int stage)
    {
        var precedingAnswers = new[] { "2", "League", "smile", "Other", "heart" };
        var answers = new Queue<string?>(precedingAnswers.Take(stage).Append("  CANCEL  "));
        var replies = new List<string>();
        var resolvedRoles = 0;
        var resolvedEmotes = 0;
        var publications = 0;

        var setup = await LegacyRoleSetupInput.RunAsync(
            () => Task.FromResult(answers.Dequeue()),
            message => message,
            message =>
            {
                resolvedRoles++;
                return Task.FromResult<LegacyRoleSetupInput.RoleChoice?>(message switch
                {
                    "League" => new(1, "League"),
                    "Other" => new(2, "Other"),
                    _ => null
                });
            },
            message =>
            {
                resolvedEmotes++;
                return Task.FromResult<ulong?>(message switch
                {
                    "smile" => 10,
                    "heart" => 20,
                    _ => null
                });
            },
            message => { replies.Add(message); return Task.CompletedTask; });

        if (setup is not null)
        {
            publications++;
        }

        Assert.Null(setup);
        Assert.Equal(0, publications);
        Assert.Empty(answers);
        Assert.Equal(1, replies.Count(message => message == "Setup cancelled"));
        Assert.Equal(stage / 2, resolvedRoles);
        Assert.Equal(Math.Max(0, (stage - 1) / 2), resolvedEmotes);
        Assert.Equal(stage + 1, replies.Count(message => message.Contains("Reply cancel to stop.")));
    }

    [Fact]
    public async Task CompleteSetup_ReturnsPairsAndLabelForPublication()
    {
        var answers = new Queue<string?>(["1", "League", "smile", "Games"]);
        var setup = await LegacyRoleSetupInput.RunAsync(
            () => Task.FromResult(answers.Dequeue()),
            message => message,
            _ => Task.FromResult<LegacyRoleSetupInput.RoleChoice?>(new(1, "League")),
            _ => Task.FromResult<ulong?>(10),
            _ => Task.CompletedTask);

        Assert.NotNull(setup);
        Assert.Equal("Games", setup.Label);
        var pair = Assert.Single(setup.Pairs);
        Assert.Equal("1", pair.RoleId);
        Assert.Equal("10", pair.EmojiId);
        Assert.Empty(answers);
    }
    [Fact]
    public async Task InvalidCountThenCorrection_CompletesWithinSameWizard()
    {
        var answers = new Queue<string?>(["0", "1", "League", "smile", "Games"]);
        var replies = new List<string>();
        var setup = await LegacyRoleSetupInput.RunAsync(
            () => Task.FromResult(answers.Dequeue()),
            message => message,
            _ => Task.FromResult<LegacyRoleSetupInput.RoleChoice?>(new(1, "League")),
            _ => Task.FromResult<ulong?>(10),
            message => { replies.Add(message); return Task.CompletedTask; });

        Assert.NotNull(setup);
        Assert.Single(setup.Pairs);
        Assert.Empty(answers);
        Assert.Single(replies, message => message.Contains("whole number from 1 to 25"));
        Assert.Single(replies, message => message.StartsWith("How many roles", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("invalid-limit")]
    [InlineData("validation")]
    [InlineData("exception")]
    [InlineData("success")]
    public async Task SessionIsHeldThroughWizardAndCanRestartAfterTerminalPath(string outcome)
    {
        using var sessions = new BoundedInteractionSessionRegistry(1);
        Assert.Equal(InteractionSessionAcquireResult.Acquired, sessions.Acquire(10, 20, out var firstLease));
        var answers = new Queue<string?>(outcome switch
        {
            "cancel" => ["cancel"],
            "timeout" => [null],
            "invalid-limit" => ["0", "26", "oops"],
            "validation" => ["1", "missing"],
            "exception" => ["1", "League"],
            _ => ["1", "League", "smile", "Games"]
        });
        var duplicateChecks = 0;
        async Task Run()
        {
            await LegacyRoleSetupInput.RunAsync(
                () => Task.FromResult(answers.Dequeue()),
                message => message,
                message => outcome == "exception"
                    ? throw new InvalidOperationException("role lookup failed")
                    : Task.FromResult<LegacyRoleSetupInput.RoleChoice?>(
                        message == "missing" ? null : new(1, "League")),
                _ => Task.FromResult<ulong?>(10),
                _ =>
                {
                    duplicateChecks++;
                    Assert.Equal(
                        InteractionSessionAcquireResult.AlreadyActive,
                        sessions.Acquire(10, 20, out var duplicateLease));
                    Assert.Null(duplicateLease);
                    return Task.CompletedTask;
                });
        }

        try
        {
            if (outcome == "exception")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(Run);
            }
            else
            {
                await Run();
            }
        }
        finally
        {
            firstLease!.Dispose();
        }

        Assert.True(duplicateChecks > 0);
        Assert.Empty(answers);
        Assert.Equal(InteractionSessionAcquireResult.Acquired, sessions.Acquire(10, 20, out var nextLease));
        firstLease!.Dispose();
        Assert.Equal(InteractionSessionAcquireResult.AlreadyActive, sessions.Acquire(10, 20, out _));
        nextLease!.Dispose();
        Assert.Equal(0, sessions.ActiveCount);
    }

}
