using System.Net;
using System.Net.Sockets;
using BeanBot.Configuration;
using BeanBot.Discord.Lifecycle;
using BeanBot.Health;
using BeanBot.Hosting;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeanBot.Tests.Integration;

public class BeanBotHostIntegrationTests
{
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(1);

    private static readonly DiscordHealthSnapshot UnhealthySnapshot = new(
        false,
        "Discord gateway has not reached the Ready state yet.",
        "LoggedOut",
        "Disconnected",
        null,
        null,
        null,
        null);

    private static readonly DiscordHealthSnapshot HealthySnapshot = new(
        true,
        "BeanBot is connected to Discord.",
        "LoggedIn",
        "Connected",
        new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
        null,
        null,
        null);

    [Fact]
    public async Task ValidConfiguration_StartsHealthTransitionsAndShutsDownInOrder()
    {
        var runtime = new RecordingRuntime(healthEnabled: true);
        await using var testHost = CreateHost(runtime, CreateValidConfiguration());

        await testHost.Host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var healthPort = runtime.HealthPort;
        using var client = CreateHealthClient(healthPort);

        using var unhealthyResponse = await client.GetAsync("/healthz");
        runtime.SetHealthSnapshot(HealthySnapshot);
        await Task.Delay(HealthPollInterval + TimeSpan.FromMilliseconds(1));
        using var healthyResponse = await client.GetAsync("/healthz");

        await testHost.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await AssertListenerStoppedAsync(healthPort);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, healthyResponse.StatusCode);
        Assert.Equal(0, runtime.HealthPort);
        Assert.Equal(
            new[]
            {
                "subscribe-events",
                "start-health",
                "start-discord",
                "start-recovery",
                "start-commands",
                "start-event-background",
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
    public async Task InvalidConfiguration_FailsBeforeRuntimeStartupWithoutExposingValues()
    {
        const string testToken = "integration-token-must-not-appear";
        const string malformedChannelId = "invalid-channel-must-not-appear";
        var configuration = CreateValidConfiguration();
        configuration[BeanBotConfiguration.BotTokenVariable] = testToken;
        configuration[BeanBotConfiguration.GeneralChannelVariable] = malformedChannelId;
        var runtime = new RecordingRuntime(healthEnabled: false);
        await using var testHost = CreateHost(runtime, configuration);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => testHost.Host.StartAsync());

        Assert.Contains(BeanBotConfiguration.GeneralChannelVariable, exception.Message);
        Assert.DoesNotContain(testToken, exception.Message);
        Assert.DoesNotContain(malformedChannelId, exception.Message);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task DiscordRetryExhaustion_FailsHostStartupAndCleansUpOnce()
    {
        var finalFailure = CreateHttpException(HttpStatusCode.BadGateway);
        var lifecycle = new QueueStartupLifecycle(
            CreateHttpException(HttpStatusCode.ServiceUnavailable),
            CreateHttpException(HttpStatusCode.TooManyRequests),
            finalFailure);
        var delay = new RecordingDelay();
        var startupService = CreateStartupService(lifecycle, delay);
        var runtime = new RecordingRuntime(healthEnabled: false)
        {
            StartDiscord = startupService.StartAsync
        };
        await using var testHost = CreateHost(runtime, CreateValidConfiguration());

        var exception = await Assert.ThrowsAsync<global::Discord.Net.HttpException>(
            () => testHost.Host.StartAsync());

        Assert.Same(finalFailure, exception);
        Assert.Equal(3, lifecycle.LoginCount);
        Assert.Equal(0, lifecycle.StartCount);
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) },
            delay.RequestedDelays);
        Assert.Equal(1, runtime.Calls.Count(call => call == "unsubscribe-events"));
        Assert.Equal(1, runtime.Calls.Count(call => call == "stop-health"));
        Assert.Equal(1, runtime.Calls.Count(call => call == "dispose-discord"));
    }

    [Fact]
    public async Task StopApplicationDuringDiscordRetry_CancelsStartupAndCleansUpOnce()
    {
        var lifecycle = new QueueStartupLifecycle(
            CreateHttpException(HttpStatusCode.ServiceUnavailable));
        var delay = new BlockingDelay();
        var startupService = CreateStartupService(lifecycle, delay);
        var runtime = new RecordingRuntime(healthEnabled: false)
        {
            StartDiscord = startupService.StartAsync
        };
        await using var testHost = CreateHost(runtime, CreateValidConfiguration());

        var startup = testHost.Host.StartAsync();
        await delay.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        testHost.Host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => startup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, lifecycle.LoginCount);
        Assert.Equal(0, lifecycle.StartCount);
        Assert.Equal(1, runtime.Calls.Count(call => call == "unsubscribe-events"));
        Assert.Equal(1, runtime.Calls.Count(call => call == "stop-health"));
        Assert.Equal(1, runtime.Calls.Count(call => call == "dispose-discord"));
    }

    private static TestHostScope CreateHost(
        RecordingRuntime runtime,
        IReadOnlyDictionary<string, string?> configurationValues)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configurationValues);
        builder.Configuration.AddBeanBotConfiguration(Array.Empty<string>());
        builder.Services.AddBeanBot(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton<IBeanBotRuntime>(runtime));

        var host = builder.Build();
        return new TestHostScope(
            host,
            host.Services.GetRequiredService<DiscordSocketClient>(),
            runtime);
    }

    private static Dictionary<string, string?> CreateValidConfiguration()
        => new()
        {
            [BeanBotConfiguration.BotTokenVariable] = "integration-test-token",
            [BeanBotConfiguration.MongoConnectionVariable] = "mongodb://127.0.0.1:27017",
            [BeanBotConfiguration.GeneralChannelVariable] = "123456789",
            [BeanBotConfiguration.HatoeteUrlVariable] = "https://example.test/hatoete.png",
            [BeanBotConfiguration.YoshimaruUrlVariable] = "https://example.test/yoshimaru.png"
        };

    private static HttpClient CreateHealthClient(int port)
        => new(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };

    private static async Task AssertListenerStoppedAsync(int port)
    {
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(
            () => client
                .ConnectAsync(IPAddress.Loopback, port)
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static DiscordStartupService CreateStartupService(
        IDiscordStartupLifecycle lifecycle,
        IDiscordStartupDelay delay)
        => new(
            lifecycle,
            NullLogger<DiscordStartupService>.Instance,
            DiscordStartupOptions.Default,
            delay);

    private static global::Discord.Net.HttpException CreateHttpException(HttpStatusCode statusCode)
        => new(statusCode, null, null, "Test Discord failure", null);

    private sealed class TestHostScope : IAsyncDisposable
    {
        private readonly DiscordSocketClient _discordClient;
        private readonly RecordingRuntime _runtime;

        public TestHostScope(
            IHost host,
            DiscordSocketClient discordClient,
            RecordingRuntime runtime)
        {
            Host = host;
            _discordClient = discordClient;
            _runtime = runtime;
        }

        public IHost Host { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Host is IAsyncDisposable asyncDisposableHost)
                {
                    await asyncDisposableHost.DisposeAsync();
                }
                else
                {
                    Host.Dispose();
                }
            }
            finally
            {
                await _runtime.DisposeAsync();
                _discordClient.Dispose();
            }
        }
    }

    private sealed class RecordingRuntime : IBeanBotRuntime, IAsyncDisposable
    {
        private readonly HealthCheckServer _healthServer;
        private DiscordHealthSnapshot _healthSnapshot = UnhealthySnapshot;

        public RecordingRuntime(bool healthEnabled)
        {
            var options = healthEnabled
                ? new HealthCheckOptions(
                    true,
                    IPAddress.Loopback,
                    0,
                    null,
                    HealthPollInterval)
                : HealthCheckOptions.Disabled;
            _healthServer = new HealthCheckServer(
                options,
                () => Volatile.Read(ref _healthSnapshot),
                NullLogger<HealthCheckServer>.Instance);
        }

        public List<string> Calls { get; } = [];
        public Func<CancellationToken, Task>? StartDiscord { get; init; }
        public bool HasActiveDiscordLifecycleOperation => false;
        public bool CanDisposeDiscordClient => true;
        public int HealthPort => _healthServer.BoundPort;

        public void SetHealthSnapshot(DiscordHealthSnapshot snapshot)
            => Volatile.Write(ref _healthSnapshot, snapshot);

        public void SubscribeApplicationEvents() => Calls.Add("subscribe-events");

        public async Task StartHealthServerAsync(CancellationToken cancellationToken)
        {
            Calls.Add("start-health");
            await _healthServer.StartAsync(cancellationToken);
        }

        public Task StartDiscordAsync(CancellationToken cancellationToken)
        {
            Calls.Add("start-discord");
            return StartDiscord?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public void StartGatewayRecovery() => Calls.Add("start-recovery");

        public Task StartCommandServicesAsync()
        {
            Calls.Add("start-commands");
            return Task.CompletedTask;
        }

        public void StartEventAndBackgroundServices() => Calls.Add("start-event-background");
        public void StopReactionServices() => Calls.Add("stop-reaction");
        public void StopNewMemberEvents() => Calls.Add("stop-new-member");
        public void StopEditedMessageEvents() => Calls.Add("stop-edited-message");
        public void StopCommandServices() => Calls.Add("stop-command");
        public void StopMessageWaiter() => Calls.Add("stop-message-waiter");
        public void StopPaginator() => Calls.Add("stop-paginator");
        public void UnsubscribeDiscordLog() => Calls.Add("unsubscribe-discord-log");

        public Task StopGatewayRecoveryAsync()
        {
            Calls.Add("stop-recovery");
            return Task.CompletedTask;
        }

        public void UnsubscribeApplicationEvents() => Calls.Add("unsubscribe-events");

        public Task StopPunServiceAsync()
        {
            Calls.Add("stop-pun");
            return Task.CompletedTask;
        }

        public async Task StopHealthServerAsync(CancellationToken cancellationToken)
        {
            Calls.Add("stop-health");
            await _healthServer.StopAsync(cancellationToken);
        }

        public Task FlushOwnerAlertsAsync()
        {
            Calls.Add("flush-alerts");
            return Task.CompletedTask;
        }

        public Task StopDiscordAsync(CancellationToken cancellationToken)
        {
            Calls.Add("stop-discord");
            return Task.CompletedTask;
        }

        public void DisposeDiscordClient() => Calls.Add("dispose-discord");

        public ValueTask DisposeAsync() => _healthServer.DisposeAsync();
    }

    private sealed class QueueStartupLifecycle : IDiscordStartupLifecycle
    {
        private readonly Queue<Exception?> _loginFailures;

        public QueueStartupLifecycle(params Exception?[] loginFailures)
        {
            _loginFailures = new Queue<Exception?>(loginFailures);
        }

        public int LoginCount { get; private set; }
        public int StartCount { get; private set; }

        public Task LoginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoginCount++;
            var failure = _loginFailures.Count > 0 ? _loginFailures.Dequeue() : null;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return Task.CompletedTask;
        }

        public Task SetPresenceAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDelay : IDiscordStartupDelay
    {
        public List<TimeSpan> RequestedDelays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedDelays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDelay : IDiscordStartupDelay
    {
        public TaskCompletionSource Requested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Requested.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
