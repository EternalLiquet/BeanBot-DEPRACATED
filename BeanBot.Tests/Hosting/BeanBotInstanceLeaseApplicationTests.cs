using BeanBot.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotInstanceLeaseApplicationTests
{
    [Fact]
    public async Task StartAsync_AcquiresLeaseAfterDiscordBeforeSideEffectAdmission()
    {
        var runtime = new RecordingRuntime();
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await application.StartAsync(CancellationToken.None);

        Assert.Equal(
            [
                "subscribe-events",
                "start-health",
                "start-discord",
                "acquire-lease",
                "start-recovery",
                "start-commands",
                "start-event-background"
            ],
            runtime.Calls);
    }

    [Fact]
    public async Task StartAsync_LeaseConflictPreventsAllSideEffectingServices()
    {
        var runtime = new RecordingRuntime
        {
            LeaseAcquisitionFailure = new InvalidOperationException("lease conflict")
        };
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => application.StartAsync(CancellationToken.None));

        Assert.Same(runtime.LeaseAcquisitionFailure, exception);
        Assert.Equal(
            ["subscribe-events", "start-health", "start-discord", "acquire-lease"],
            runtime.Calls);
        Assert.DoesNotContain("start-recovery", runtime.Calls);
        Assert.DoesNotContain("start-commands", runtime.Calls);
        Assert.DoesNotContain("start-event-background", runtime.Calls);
    }

    [Fact]
    public async Task StopAsync_DrainedRuntimeReleasesLeaseAfterSideEffectsBeforeHealthAndDiscord()
    {
        var runtime = new RecordingRuntime();
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await application.StopAsync(CancellationToken.None);

        Assert.Equal(
            [
                "stop-reaction",
                "stop-new-member",
                "stop-edited-message",
                "stop-command",
                "stop-command-replies",
                "stop-message-waiter",
                "stop-paginator",
                "unsubscribe-discord-log",
                "stop-recovery",
                "unsubscribe-events",
                "stop-pun",
                "flush-alerts",
                "release-lease",
                "stop-health",
                "stop-discord",
                "dispose-discord",
                "flush-alerts"
            ],
            runtime.Calls);
    }

    [Fact]
    public async Task StopAsync_UndrainedCommandWorkKeepsLeaseForExpiryFallback()
    {
        var runtime = new RecordingRuntime { CommandServicesDrained = false };
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(
            () => application.StopAsync(CancellationToken.None));

        Assert.Contains("skip-lease-release", runtime.Calls);
        Assert.DoesNotContain("release-lease", runtime.Calls);
        Assert.DoesNotContain("stop-discord", runtime.Calls);
        Assert.DoesNotContain("dispose-discord", runtime.Calls);
    }

    [Fact]
    public async Task StopAsync_ActiveDiscordOperationKeepsLeaseForExpiryFallback()
    {
        var runtime = new RecordingRuntime { HasActiveDiscordLifecycleOperation = true };
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await application.StopAsync(CancellationToken.None);

        Assert.Contains("skip-lease-release", runtime.Calls);
        Assert.DoesNotContain("release-lease", runtime.Calls);
        Assert.DoesNotContain("stop-discord", runtime.Calls);
    }

    private sealed class RecordingRuntime : IBeanBotRuntime
    {
        public List<string> Calls { get; } = [];
        public bool HasActiveDiscordLifecycleOperation { get; init; }
        public bool CanDisposeDiscordClient => true;
        public bool CommandServicesDrained { get; init; } = true;
        public InvalidOperationException? LeaseAcquisitionFailure { get; init; }

        public void SubscribeApplicationEvents() => Calls.Add("subscribe-events");
        public Task StartHealthServerAsync(CancellationToken cancellationToken) => RecordAsync("start-health");
        public Task StartDiscordAsync(CancellationToken cancellationToken) => RecordAsync("start-discord");

        public Task AcquireInstanceLeaseAsync(CancellationToken cancellationToken)
        {
            Calls.Add("acquire-lease");
            return LeaseAcquisitionFailure is null
                ? Task.CompletedTask
                : Task.FromException(LeaseAcquisitionFailure);
        }

        public void StartGatewayRecovery() => Calls.Add("start-recovery");
        public Task StartCommandServicesAsync() => RecordAsync("start-commands");
        public void StartEventAndBackgroundServices() => Calls.Add("start-event-background");
        public void StopReactionServices() => Calls.Add("stop-reaction");
        public void StopNewMemberEvents() => Calls.Add("stop-new-member");
        public Task StopEditedMessageEventsAsync() => RecordAsync("stop-edited-message");

        public async Task<bool> StopCommandServicesAsync()
        {
            await RecordAsync("stop-command");
            return CommandServicesDrained;
        }

        public Task StopCommandRepliesAsync() => RecordAsync("stop-command-replies");
        public void StopMessageWaiter() => Calls.Add("stop-message-waiter");
        public void StopPaginator() => Calls.Add("stop-paginator");
        public void UnsubscribeDiscordLog() => Calls.Add("unsubscribe-discord-log");
        public Task StopGatewayRecoveryAsync() => RecordAsync("stop-recovery");
        public void UnsubscribeApplicationEvents() => Calls.Add("unsubscribe-events");
        public Task StopPunServiceAsync() => RecordAsync("stop-pun");
        public Task ReleaseInstanceLeaseAsync(CancellationToken cancellationToken) => RecordAsync("release-lease");
        public void SkipInstanceLeaseRelease() => Calls.Add("skip-lease-release");
        public Task StopHealthServerAsync(CancellationToken cancellationToken) => RecordAsync("stop-health");
        public Task FlushOwnerAlertsAsync() => RecordAsync("flush-alerts");
        public Task StopDiscordAsync(CancellationToken cancellationToken) => RecordAsync("stop-discord");
        public void DisposeDiscordClient() => Calls.Add("dispose-discord");

        private Task RecordAsync(string operation)
        {
            Calls.Add(operation);
            return Task.CompletedTask;
        }
    }
}
