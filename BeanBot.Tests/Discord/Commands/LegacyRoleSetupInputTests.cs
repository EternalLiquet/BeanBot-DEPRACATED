using BeanBot.Discord.Commands;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class LegacyRoleSetupInputTests
{
    [Theory]
    [InlineData(0, "cancel")]
    [InlineData(1, " CaNcEl \t")]
    [InlineData(2, "cancel")]
    [InlineData(3, "cancel")]
    [InlineData(4, "cancel")]
    [InlineData(5, "cancel")]
    public async Task Cancellation_EndsAtEveryWizardStageBeforeNextPrompt(int stage, string cancellation)
    {
        var precedingAnswers = new[] { "2", "League", "smile", "Other", "heart" };
        var answers = new Queue<string?>(precedingAnswers.Take(stage).Append(cancellation));
        var replies = new List<string>();
        var waits = 0;
        Task<string?> Wait()
        {
            waits++;
            return Task.FromResult(answers.Dequeue());
        }

        var count = await LegacyRoleSetupInput.ReadCountAsync(Wait, message => message, Send);
        if (stage == 0)
        {
            Assert.Equal(LegacyRoleSetupInput.AnswerStatus.Cancelled, count.Status);
        }
        else
        {
            Assert.Equal(LegacyRoleSetupInput.AnswerStatus.Accepted, count.Status);
            for (var answerIndex = 1; answerIndex <= stage; answerIndex++)
            {
                var answer = await LegacyRoleSetupInput.ReadNextAsync(Wait, message => message, Send);
                Assert.Equal(answerIndex == stage
                    ? LegacyRoleSetupInput.AnswerStatus.Cancelled
                    : LegacyRoleSetupInput.AnswerStatus.Accepted, answer.Status);
            }
        }

        Assert.Equal(stage + 1, waits);
        Assert.Empty(answers);
        Assert.Equal(["Setup cancelled"], replies);

        Task Send(string message)
        {
            replies.Add(message);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("25", 25)]
    public async Task Count_AcceptsBounds(string input, int expected)
    {
        var replies = new List<string>();
        var result = await LegacyRoleSetupInput.ReadCountAsync(() => Task.FromResult<string?>(input), message => message, message => { replies.Add(message); return Task.CompletedTask; });
        Assert.Equal(LegacyRoleSetupInput.AnswerStatus.Accepted, result.Status);
        Assert.Equal(expected, result.Count);
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("26")]
    [InlineData("not a number")]
    public async Task Count_InvalidThenValid_ReusesSameInputFlow(string invalid)
    {
        var answers = new Queue<string?>([invalid, "2"]);
        var replies = new List<string>();
        var result = await LegacyRoleSetupInput.ReadCountAsync(
            () => Task.FromResult(answers.Dequeue()), message => message, message => { replies.Add(message); return Task.CompletedTask; });
        Assert.Equal(LegacyRoleSetupInput.AnswerStatus.Accepted, result.Status);
        Assert.Equal(2, result.Count);
        Assert.Empty(answers);
        Assert.Single(replies);
        Assert.Contains("1 to 25", replies[0]);
    }

    [Fact]
    public async Task Count_InvalidThenCancel_StopsWithoutThirdWait()
    {
        var answers = new Queue<string?>(["0", " CANCEL "]);
        var replies = new List<string>();
        var result = await LegacyRoleSetupInput.ReadCountAsync(
            () => Task.FromResult(answers.Dequeue()), message => message, message => { replies.Add(message); return Task.CompletedTask; });
        Assert.Equal(LegacyRoleSetupInput.AnswerStatus.Cancelled, result.Status);
        Assert.Equal(2, replies.Count);
        Assert.Equal("Setup cancelled", replies[1]);
    }

    [Fact]
    public async Task Count_ThirdInvalidAnswer_EndsWithRestartInstruction()
    {
        var answers = new Queue<string?>(["0", "26", "no"]);
        var replies = new List<string>();
        var result = await LegacyRoleSetupInput.ReadCountAsync(
            () => Task.FromResult(answers.Dequeue()), message => message, message => { replies.Add(message); return Task.CompletedTask; });
        Assert.Equal(LegacyRoleSetupInput.AnswerStatus.InvalidCountLimit, result.Status);
        Assert.Empty(answers);
        Assert.Equal(3, replies.Count);
        Assert.Contains("start again", replies[^1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeout_EndsInputAndDoesNotWaitAgain()
    {
        var waits = 0;
        var replies = new List<string>();
        var result = await LegacyRoleSetupInput.ReadCountAsync(() =>
        {
            waits++;
            return Task.FromResult<string?>(null);
        }, message => message, message => { replies.Add(message); return Task.CompletedTask; });
        Assert.Equal(LegacyRoleSetupInput.AnswerStatus.TimedOut, result.Status);
        Assert.Equal(1, waits);
        Assert.Single(replies);
        Assert.Contains("expired", replies[0], StringComparison.OrdinalIgnoreCase);
    }
}
