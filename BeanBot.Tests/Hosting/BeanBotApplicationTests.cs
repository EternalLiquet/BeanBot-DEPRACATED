using BeanBot.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotApplicationTests
{
    [Fact]
    public async Task StartAsync_StartsHealthBeforeDiscordAndInitializesInOrderOnce()
    {
        var runtime = new RecordingRuntime();
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await application.StartAsync(CancellationToken.None);
        await application.StartAsync(CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "subscribe-events",
                "start-health",
                "start-discord",
                "start-recovery",
                "start-commands",
                "start-event-background"
            },
            runtime.Calls);
    }

    [Fact]
    public async Task StopAsync_NormalShutdown_CleansUpInOrderOnce()
    {
        var runtime = new RecordingRuntime();
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await application.StopAsync(CancellationToken.None);
        await application.StopAsync(CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "stop-reaction",
                "stop-new-member",
                "stop-edited-message",
                "stop-command",
                "stop-message-waiter",
                "stop-paginator",
                "unsubscribe-discord-log",
                "stop-recovery",
                "unsubscribe-events",
                "stop-pun",
                "stop-health",
                "flush-alerts",
                "stop-discord",
                "dispose-discord",
                "flush-alerts"
            },
            runtime.Calls);
    }

    [Fact]
    public async Task StopAsync_UnfinishedDiscordStartup_SkipsDiscordShutdownAndDisposal()
    {
        var runtime = new RecordingRuntime
        {
            HasUnfinishedDiscordStartupOperation = true
        };
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await application.StopAsync(CancellationToken.None);

        Assert.DoesNotContain("stop-discord", runtime.Calls);
        Assert.DoesNotContain("dispose-discord", runtime.Calls);
        Assert.Equal(2, runtime.Calls.Count(call => call == "flush-alerts"));
        Assert.Contains("unsubscribe-events", runtime.Calls);
    }

    [Theory]
    [InlineData("stop-reaction", "stop-new-member")]
    [InlineData("stop-new-member", "stop-edited-message")]
    [InlineData("stop-edited-message", "stop-command")]
    [InlineData("stop-command", "stop-message-waiter")]
    [InlineData("stop-message-waiter", "stop-paginator")]
    [InlineData("stop-paginator", "unsubscribe-discord-log")]
    [InlineData("unsubscribe-discord-log", "stop-recovery")]
    [InlineData("stop-recovery", "unsubscribe-events")]
    [InlineData("unsubscribe-events", "stop-pun")]
    [InlineData("stop-pun", "stop-health")]
    [InlineData("stop-health", "flush-alerts")]
    [InlineData("flush-alerts", "stop-discord")]
    [InlineData("stop-discord", "dispose-discord")]
    [InlineData("dispose-discord", "flush-alerts")]
    public async Task StopAsync_StageFailure_ContinuesLaterSafeCleanupAndPreservesFirstFailure(
        string failingOperation,
        string expectedLaterOperation)
    {
        var runtime = new RecordingRuntime { FailingOperation = failingOperation };
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => application.StopAsync(CancellationToken.None));

        Assert.Same(runtime.Failure, exception);
        var failureIndex = runtime.Calls.IndexOf(failingOperation);
        Assert.True(failureIndex >= 0);
        Assert.Contains(expectedLaterOperation, runtime.Calls.Skip(failureIndex + 1));
    }

    [Fact]
    public async Task StopAsync_HostCancellation_SkipsNewNetworkCleanupButDisposesClient()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new RecordingRuntime
        {
            CancelOnOperation = "stop-health",
            ShutdownCancellation = cancellation
        };
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => application.StopAsync(cancellation.Token));

        Assert.DoesNotContain("flush-alerts", runtime.Calls);
        Assert.DoesNotContain("stop-discord", runtime.Calls);
        Assert.Contains("dispose-discord", runtime.Calls);
    }

    [Fact]
    public async Task StopAsync_StageExceedsBound_ContinuesCleanupAndObservesTimeout()
    {
        var runtime = new RecordingRuntime { IncompleteOperation = "stop-pun" };
        var application = new BeanBotApplication(
            runtime,
            NullLogger<BeanBotApplication>.Instance,
            TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(
            () => application.StopAsync(CancellationToken.None));

        Assert.Contains("stop-health", runtime.Calls);
        Assert.Contains("dispose-discord", runtime.Calls);
        Assert.Contains("flush-alerts", runtime.Calls);
    }

    [Fact]
    public async Task StopAsync_SynchronousStageBlocks_ContinuesCleanupAfterBound()
    {
        using var blockingRelease = new ManualResetEventSlim();
        var runtime = new RecordingRuntime
        {
            BlockingOperation = "stop-reaction",
            BlockingRelease = blockingRelease
        };
        var application = new BeanBotApplication(
            runtime,
            NullLogger<BeanBotApplication>.Instance,
            TimeSpan.FromMilliseconds(25));

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => application.StopAsync(CancellationToken.None));

            Assert.Contains("stop-new-member", runtime.Calls);
            Assert.Contains("stop-health", runtime.Calls);
            Assert.Contains("dispose-discord", runtime.Calls);
        }
        finally
        {
            blockingRelease.Set();
        }
    }

    [Theory]
    [InlineData("start-discord")]
    [InlineData("start-commands")]
    public async Task StartupFailure_AllowsCompletePartialStartupCleanup(string failingOperation)
    {
        var runtime = new RecordingRuntime { FailingOperation = failingOperation };
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => application.StartAsync(CancellationToken.None));
        await application.StopAsync(CancellationToken.None);

        Assert.Contains("stop-reaction", runtime.Calls);
        Assert.Contains("stop-recovery", runtime.Calls);
        Assert.Contains("unsubscribe-events", runtime.Calls);
        Assert.Contains("stop-pun", runtime.Calls);
        Assert.Contains("stop-health", runtime.Calls);
        Assert.Equal(1, runtime.Calls.Count(call => call == "unsubscribe-events"));
    }

    private sealed class RecordingRuntime : IBeanBotRuntime
    {
        public List<string> Calls { get; } = [];
        public string? FailingOperation { get; init; }
        public string? CancelOnOperation { get; init; }
        public string? IncompleteOperation { get; init; }
        public string? BlockingOperation { get; init; }
        public ManualResetEventSlim? BlockingRelease { get; init; }
        public CancellationTokenSource? ShutdownCancellation { get; init; }
        public InvalidOperationException Failure { get; } = new("injected shutdown failure");
        public bool HasUnfinishedDiscordStartupOperation { get; init; }
        private bool _failureThrown;

        public void SubscribeApplicationEvents() => Record("subscribe-events");
        public Task StartHealthServerAsync(CancellationToken cancellationToken)
            => RecordAsync("start-health");
        public Task StartDiscordAsync(CancellationToken cancellationToken)
            => RecordAsync("start-discord");
        public void StartGatewayRecovery() => Record("start-recovery");
        public Task StartCommandServicesAsync() => RecordAsync("start-commands");
        public void StartEventAndBackgroundServices() => Record("start-event-background");
        public void StopReactionServices() => Record("stop-reaction");
        public void StopNewMemberEvents() => Record("stop-new-member");
        public void StopEditedMessageEvents() => Record("stop-edited-message");
        public void StopCommandServices() => Record("stop-command");
        public void StopMessageWaiter() => Record("stop-message-waiter");
        public void StopPaginator() => Record("stop-paginator");
        public void UnsubscribeDiscordLog() => Record("unsubscribe-discord-log");
        public Task StopGatewayRecoveryAsync() => RecordAsync("stop-recovery");
        public void UnsubscribeApplicationEvents() => Record("unsubscribe-events");
        public Task StopPunServiceAsync() => RecordAsync("stop-pun");
        public Task StopHealthServerAsync(CancellationToken cancellationToken)
            => RecordAsync("stop-health");
        public Task FlushOwnerAlertsAsync() => RecordAsync("flush-alerts");
        public Task StopDiscordAsync(CancellationToken cancellationToken)
            => RecordAsync("stop-discord");
        public void DisposeDiscordClient() => Record("dispose-discord");

        private void Record(string operation)
        {
            Calls.Add(operation);
            if (BlockingOperation == operation)
            {
                BlockingRelease?.Wait();
            }
            if (FailingOperation == operation && !_failureThrown)
            {
                _failureThrown = true;
                throw Failure;
            }
            if (CancelOnOperation == operation)
            {
                ShutdownCancellation?.Cancel();
                throw new OperationCanceledException(ShutdownCancellation?.Token ?? default);
            }
        }

        private Task RecordAsync(string operation)
        {
            if (IncompleteOperation == operation)
            {
                Calls.Add(operation);
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }

            try
            {
                Record(operation);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }
    }
}
