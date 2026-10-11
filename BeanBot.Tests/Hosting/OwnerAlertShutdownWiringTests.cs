using System.Reflection;
using BeanBot.Hosting;
using BeanBot.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class OwnerAlertShutdownWiringTests
{
    [Fact]
    public async Task ApplicationShutdown_StopsPendingRetryAndQueuedAlertBeforeDiscordDisposal()
    {
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new FailingDelivery();
        await using var notifier = new DiscordOwnerErrorNotifier(
            delivery,
            _ =>
            {
                retryStarted.TrySetResult();
                return TimeSpan.FromSeconds(10);
            },
            TimeSpan.FromMilliseconds(40));
        var (host, runtime, calls) = CreateRuntime(notifier);
        using (host)
        {
            notifier.Enqueue("first alert");
            await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            notifier.Enqueue("queued alert");

            var application = new BeanBotApplication(
                runtime, NullLogger<BeanBotApplication>.Instance, TimeSpan.FromMilliseconds(200));
            await application.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains(nameof(IBeanBotRuntime.DisposeDiscordClient), calls);
            Assert.Equal(1, delivery.CallCount);
            notifier.Enqueue("late alert");
            await Task.Delay(50);
            Assert.Equal(1, delivery.CallCount);
        }
    }

    [Fact]
    public async Task ApplicationShutdown_BlockedLateDeliveryKeepsDiscordClientOwned()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new BlockingDelivery(started, completion);
        await using var notifier = new DiscordOwnerErrorNotifier(
            delivery,
            _ => TimeSpan.Zero,
            TimeSpan.FromMilliseconds(40));
        var (host, runtime, calls) = CreateRuntime(notifier);
        using (host)
        {
            notifier.Enqueue("blocked alert");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var application = new BeanBotApplication(
                runtime, NullLogger<BeanBotApplication>.Instance, TimeSpan.FromMilliseconds(200));
            await application.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.DoesNotContain(nameof(IBeanBotRuntime.StopDiscordAsync), calls);
            Assert.DoesNotContain(nameof(IBeanBotRuntime.DisposeDiscordClient), calls);
            Assert.True(notifier.HasActiveDiscordOperation);
            completion.TrySetResult();
            Assert.False(notifier.HasActiveDiscordOperation);
        }
    }

    [Fact]
    public async Task ApplicationShutdown_NotifierStopTimeoutKeepsDiscordClientOwnedUntilWorkerStops()
    {
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new FailingDelivery();
        await using var notifier = new DiscordOwnerErrorNotifier(
            delivery,
            _ =>
            {
                retryStarted.TrySetResult();
                return TimeSpan.FromSeconds(10);
            },
            TimeSpan.FromMilliseconds(250));
        var (host, runtime, calls) = CreateRuntime(notifier);
        using (host)
        {
            notifier.Enqueue("first alert");
            await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            notifier.Enqueue("queued alert");

            var application = new BeanBotApplication(
                runtime, NullLogger<BeanBotApplication>.Instance, TimeSpan.FromMilliseconds(25));
            await Assert.ThrowsAsync<TimeoutException>(
                () => application.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.DoesNotContain(nameof(IBeanBotRuntime.StopDiscordAsync), calls);
            Assert.DoesNotContain(nameof(IBeanBotRuntime.DisposeDiscordClient), calls);
            Assert.Equal(1, delivery.CallCount);
        }
    }

    [Fact]
    public async Task ApplicationShutdown_StalledStartupReportKeepsDiscordClientOwned()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var notifier = new DiscordOwnerErrorNotifier(
            new FailingDelivery(),
            _ => TimeSpan.Zero,
            TimeSpan.FromMilliseconds(40));
        var startupReport = new BeanBotStartupReport(
            () => PunChannelStartupStatus.NotFound,
            "2.18.3",
            null,
            null,
            _ => { },
            new BlockingDelivery(started, completion),
            TimeProvider.System);
        var (host, runtime, calls) = CreateRuntime(notifier, startupReport);
        using (host)
        {
            startupReport.QueueOnFirstReady();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var application = new BeanBotApplication(
                runtime, NullLogger<BeanBotApplication>.Instance, TimeSpan.FromMilliseconds(200));
            await application.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains(nameof(IBeanBotRuntime.StopStartupReportAsync), calls);
            Assert.DoesNotContain(nameof(IBeanBotRuntime.StopDiscordAsync), calls);
            Assert.DoesNotContain(nameof(IBeanBotRuntime.DisposeDiscordClient), calls);
            Assert.True(startupReport.HasActiveDiscordOperation);
            completion.TrySetResult();
            Assert.False(startupReport.HasActiveDiscordOperation);
        }
    }

    private static (IHost Host, IBeanBotRuntime Runtime, List<string> Calls) CreateRuntime(
        DiscordOwnerErrorNotifier notifier,
        BeanBotStartupReport? startupReport = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BeanBot:BotToken"] = "test-token",
            ["BeanBot:MongoConnectionString"] = "mongodb://127.0.0.1:27017",
            ["BeanBot:GeneralChannelId"] = "1",
            ["BeanBot:HatoeteUrl"] = "https://example.com/a.png",
            ["BeanBot:YoshimaruUrl"] = "https://example.com/b.png"
        });
        builder.Services.AddBeanBot(builder.Configuration);
        builder.Services.AddSingleton(notifier);
        if (startupReport is not null)
        {
            builder.Services.AddSingleton(startupReport);
        }
        var host = builder.Build();
        var realRuntime = host.Services.GetRequiredService<IBeanBotRuntime>();
        var calls = new List<string>();
        var runtime = DispatchProxy.Create<IBeanBotRuntime, RuntimeProxy>();
        ((RuntimeProxy)runtime).InvokeMethod = method =>
        {
            calls.Add(method.Name);
            if (method.Name == "get_HasActiveDiscordLifecycleOperation")
                return realRuntime.HasActiveDiscordLifecycleOperation;
            if (method.Name == "get_CanDisposeDiscordClient") return true;
            if (method.Name == nameof(IBeanBotRuntime.FlushOwnerAlertsAsync))
                return realRuntime.FlushOwnerAlertsAsync();
            if (method.Name == nameof(IBeanBotRuntime.StopStartupReportAsync))
                return realRuntime.StopStartupReportAsync();
            if (method.ReturnType == typeof(Task<bool>)) return Task.FromResult(true);
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            return null;
        };
        return (host, runtime, calls);
    }

    public class RuntimeProxy : DispatchProxy
    {
        public Func<MethodInfo, object?>? InvokeMethod { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => InvokeMethod!(targetMethod!);
    }

    private sealed class FailingDelivery : IOwnerAlertDelivery
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException("Expected test failure");
        }
    }

    private sealed class BlockingDelivery(
        TaskCompletionSource started,
        TaskCompletionSource completion) : IOwnerAlertDelivery
    {
        public Task DeliverAsync(string alert, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            return completion.Task;
        }
    }
}
