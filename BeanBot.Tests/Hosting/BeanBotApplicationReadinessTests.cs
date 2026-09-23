using BeanBot.Health;
using BeanBot.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotApplicationReadinessTests
{
    [Fact]
    public async Task StartAsync_CompletesCoreStartupWithoutPublishingFinalReadiness()
    {
        var readiness = new ApplicationReadinessState();
        ApplicationLifecycleState? stateDuringFinalStage = null;
        var runtime = new RecordingRuntime
        {
            OperationRecorded = operation =>
            {
                if (operation == "start-event-background")
                {
                    stateDuringFinalStage = readiness.CreateSnapshot().State;
                }
            }
        };
        var application = CreateApplication(runtime, readiness);

        await application.StartAsync(CancellationToken.None);

        Assert.Equal(ApplicationLifecycleState.Starting, stateDuringFinalStage);
        Assert.Equal(ApplicationLifecycleState.Starting, readiness.CreateSnapshot().State);
    }

    [Fact]
    public async Task StartAsync_FinalStartupFailure_NeverPublishesReady()
    {
        var readiness = new ApplicationReadinessState();
        var runtime = new RecordingRuntime
        {
            FailingOperation = "start-event-background"
        };
        var application = CreateApplication(runtime, readiness);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => application.StartAsync(CancellationToken.None));

        Assert.Equal(ApplicationLifecycleState.Starting, readiness.CreateSnapshot().State);
    }

    [Fact]
    public async Task StartAsync_CancellationAtCompletionBoundary_NeverPublishesReady()
    {
        using var cancellation = new CancellationTokenSource();
        var readiness = new ApplicationReadinessState();
        var runtime = new RecordingRuntime
        {
            OperationRecorded = operation =>
            {
                if (operation == "start-event-background")
                {
                    cancellation.Cancel();
                }
            }
        };
        var application = CreateApplication(runtime, readiness);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => application.StartAsync(cancellation.Token));

        Assert.Equal(ApplicationLifecycleState.Starting, readiness.CreateSnapshot().State);
    }

    [Fact]
    public async Task StopAsync_ClearsReadyBeforeFirstUserFacingStopStageAndNeverRestoresIt()
    {
        var readiness = new ApplicationReadinessState();
        readiness.MarkReady();
        ApplicationLifecycleState? stateAtFirstStopStage = null;
        var runtime = new RecordingRuntime();
        var application = CreateApplication(runtime, readiness);
        runtime.OperationRecorded = operation =>
        {
            if (operation == "stop-reaction")
            {
                stateAtFirstStopStage = readiness.CreateSnapshot().State;
            }
        };
        runtime.FailingOperation = "stop-reaction";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => application.StopAsync(CancellationToken.None));

        Assert.Equal(ApplicationLifecycleState.Draining, stateAtFirstStopStage);
        Assert.Equal(ApplicationLifecycleState.Draining, readiness.CreateSnapshot().State);
    }

    [Fact]
    public async Task RepeatedStartAndStopCalls_KeepLifecycleTransitionsIdempotent()
    {
        var readiness = new ApplicationReadinessState();
        var runtime = new RecordingRuntime();
        var application = CreateApplication(runtime, readiness);

        await application.StartAsync(CancellationToken.None);
        await application.StartAsync(CancellationToken.None);
        readiness.MarkReady();
        await application.StopAsync(CancellationToken.None);
        await application.StopAsync(CancellationToken.None);

        Assert.Equal(1, runtime.Calls.Count(operation => operation == "start-event-background"));
        Assert.Equal(1, runtime.Calls.Count(operation => operation == "stop-reaction"));
        Assert.Equal(ApplicationLifecycleState.Draining, readiness.CreateSnapshot().State);
    }

    private static BeanBotApplication CreateApplication(
        RecordingRuntime runtime,
        ApplicationReadinessState readiness)
        => new(
            runtime,
            readiness,
            NullLogger<BeanBotApplication>.Instance,
            TimeSpan.FromSeconds(1));

    private sealed class RecordingRuntime : IBeanBotRuntime
    {
        private readonly InvalidOperationException _failure = new("injected failure");
        private bool _failureThrown;

        public List<string> Calls { get; } = [];
        public Action<string>? OperationRecorded { get; set; }
        public string? FailingOperation { get; set; }
        public bool HasActiveDiscordLifecycleOperation => false;
        public bool CanDisposeDiscordClient => true;

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
        public Task StopEditedMessageEventsAsync() => RecordAsync("stop-edited-message");

        public async Task<bool> StopCommandServicesAsync()
        {
            await RecordAsync("stop-command");
            return true;
        }

        public Task StopCommandRepliesAsync() => RecordAsync("stop-command-replies");
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
            OperationRecorded?.Invoke(operation);
            if (FailingOperation == operation && !_failureThrown)
            {
                _failureThrown = true;
                throw _failure;
            }
        }

        private Task RecordAsync(string operation)
        {
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
