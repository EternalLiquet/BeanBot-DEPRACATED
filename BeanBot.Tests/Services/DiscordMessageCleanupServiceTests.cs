using BeanBot.Services;
using Xunit;

namespace BeanBot.Tests.Services;

public class DiscordMessageCleanupServiceTests
{
    [Theory]
    [InlineData(0, new int[0], 0)]
    [InlineData(1, new int[0], 1)]
    [InlineData(100, new[] { 100 }, 0)]
    [InlineData(101, new[] { 100 }, 1)]
    [InlineData(105, new[] { 100, 5 }, 0)]
    [InlineData(200, new[] { 100, 100 }, 0)]
    public void CreatePlan_UsesValidDiscordBulkDeleteBatches(
        int messageCount,
        int[] expectedBatchSizes,
        int expectedIndividualCount)
    {
        var now = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, messageCount)
            .Select(index => new TestMessage(index, now))
            .ToArray();

        var plan = DiscordMessageCleanupService.CreatePlan(messages, message => message.Timestamp, now);

        Assert.Equal(expectedBatchSizes, plan.Batches.Select(batch => batch.Count));
        Assert.Equal(expectedIndividualCount, plan.IndividualItems.Count);
        Assert.All(plan.Batches, batch => Assert.InRange(batch.Count, 2, 100));
    }

    [Fact]
    public void CreatePlan_DeletesMessagesOlderThanDiscordLimitIndividually()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = new TestMessage(1, now.Subtract(TimeSpan.FromDays(1)));
        var old = new TestMessage(2, now.Subtract(TimeSpan.FromDays(14)));

        var plan = DiscordMessageCleanupService.CreatePlan(
            new[] { recent, old },
            message => message.Timestamp,
            now);

        Assert.Empty(plan.Batches);
        Assert.Equal(new[] { 1, 2 }, plan.IndividualItems.Select(message => message.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task ExecutePlan_ContinuesAfterBatchAndIndividualFailures()
    {
        var now = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, 101)
            .Select(index => new TestMessage(index, now))
            .ToArray();
        var plan = DiscordMessageCleanupService.CreatePlan(messages, message => message.Timestamp, now);
        var attemptedBatches = new List<int>();
        var attemptedIndividuals = new List<int>();
        var failures = new List<(int Count, bool IsBatch)>();

        await DiscordMessageCleanupService.ExecutePlanAsync(
            plan,
            batch =>
            {
                attemptedBatches.Add(batch.Count);
                throw new InvalidOperationException("bulk failed");
            },
            message =>
            {
                attemptedIndividuals.Add(message.Id);
                return message.Id == 100
                    ? Task.FromException(new InvalidOperationException("individual failed"))
                    : Task.CompletedTask;
            },
            (_, count, isBatch) => failures.Add((count, isBatch)));

        Assert.Equal(new[] { 100 }, attemptedBatches);
        Assert.Equal(new[] { 100 }, attemptedIndividuals);
        Assert.Equal(new[] { (100, true), (1, false) }, failures);
    }

    [Fact]
    public async Task ExecutePlan_ContinuesWithLaterIndividualsAfterOneFails()
    {
        var now = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            new TestMessage(1, now.Subtract(TimeSpan.FromDays(14))),
            new TestMessage(2, now.Subtract(TimeSpan.FromDays(14)))
        };
        var plan = DiscordMessageCleanupService.CreatePlan(messages, message => message.Timestamp, now);
        var attempted = new List<int>();

        await DiscordMessageCleanupService.ExecutePlanAsync(
            plan,
            _ => Task.CompletedTask,
            message =>
            {
                attempted.Add(message.Id);
                return message.Id == 1
                    ? Task.FromException(new InvalidOperationException("failed"))
                    : Task.CompletedTask;
            },
            (_, _, _) => { });

        Assert.Equal(new[] { 1, 2 }, attempted);
    }

    private sealed record TestMessage(int Id, DateTimeOffset Timestamp);
}
