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
                "stop-event-command",
                "stop-recovery",
                "unsubscribe-events",
                "stop-background",
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
    [InlineData("start-discord")]
    [InlineData("start-commands")]
    public async Task StartupFailure_AllowsCompletePartialStartupCleanup(string failingOperation)
    {
        var runtime = new RecordingRuntime { FailingOperation = failingOperation };
        var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => application.StartAsync(CancellationToken.None));
        await application.StopAsync(CancellationToken.None);

        Assert.Contains("stop-event-command", runtime.Calls);
        Assert.Contains("stop-recovery", runtime.Calls);
        Assert.Contains("unsubscribe-events", runtime.Calls);
        Assert.Contains("stop-background", runtime.Calls);
        Assert.Equal(1, runtime.Calls.Count(call => call == "unsubscribe-events"));
    }

    private sealed class RecordingRuntime : IBeanBotRuntime
    {
        public List<string> Calls { get; } = new();
        public string? FailingOperation { get; init; }
        public bool HasUnfinishedDiscordStartupOperation { get; init; }

        public void SubscribeApplicationEvents() => Record("subscribe-events");
        public Task StartHealthServerAsync(CancellationToken cancellationToken)
            => RecordAsync("start-health");
        public Task StartDiscordAsync(CancellationToken cancellationToken)
            => RecordAsync("start-discord");
        public void StartGatewayRecovery() => Record("start-recovery");
        public Task StartCommandServicesAsync() => RecordAsync("start-commands");
        public void StartEventAndBackgroundServices() => Record("start-event-background");
        public void StopEventAndCommandServices() => Record("stop-event-command");
        public Task StopGatewayRecoveryAsync() => RecordAsync("stop-recovery");
        public void UnsubscribeApplicationEvents() => Record("unsubscribe-events");
        public Task StopBackgroundServicesAsync(CancellationToken cancellationToken)
            => RecordAsync("stop-background");
        public Task FlushOwnerAlertsAsync() => RecordAsync("flush-alerts");
        public Task StopDiscordAsync(CancellationToken cancellationToken)
            => RecordAsync("stop-discord");
        public void DisposeDiscordClient() => Record("dispose-discord");

        private void Record(string operation)
        {
            Calls.Add(operation);
            if (FailingOperation == operation)
            {
                throw new InvalidOperationException($"{operation} failed");
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
