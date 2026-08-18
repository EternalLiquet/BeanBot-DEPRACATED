using BeanBot.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotHostedServiceTests
{
    [Fact]
    public async Task Host_StartAndStop_InvokesApplicationLifecycleOnce()
    {
        var application = new RecordingApplication();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IBeanBotApplication>(application);
        builder.Services.AddHostedService<BeanBotHostedService>();

        var host = builder.Build();
        try
        {
            await host.StartAsync();
            await host.StopAsync();
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposableHost)
            {
                await asyncDisposableHost.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }

        Assert.Equal(1, application.StartCount);
        Assert.Equal(1, application.StopCount);
        Assert.False(application.StartToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StartAsync_ForwardsCancellationToken()
    {
        var application = new RecordingApplication { BlockUntilCancelled = true };
        var hostedService = new BeanBotHostedService(
            application,
            new RecordingHostLifetime(),
            NullLogger<BeanBotHostedService>.Instance);
        using var cancellation = new CancellationTokenSource();

        var startup = hostedService.StartAsync(cancellation.Token);
        await application.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.True(application.StartToken.IsCancellationRequested);
        Assert.Equal(1, application.StopCount);
    }

    [Fact]
    public async Task StopAsync_ForwardsCancellationToken()
    {
        var application = new RecordingApplication();
        var hostedService = new BeanBotHostedService(
            application,
            new RecordingHostLifetime(),
            NullLogger<BeanBotHostedService>.Instance);
        using var cancellation = new CancellationTokenSource();

        await hostedService.StopAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, application.StopToken);
    }

    [Fact]
    public async Task StartAsync_WhenStartupFails_CleansUpAndPreservesStartupFailure()
    {
        var startupFailure = new InvalidOperationException("startup failed");
        var application = new RecordingApplication { StartFailure = startupFailure };
        var hostedService = new BeanBotHostedService(
            application,
            new RecordingHostLifetime(),
            NullLogger<BeanBotHostedService>.Instance);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => hostedService.StartAsync(CancellationToken.None));

        Assert.Same(startupFailure, thrown);
        Assert.Equal(1, application.StopCount);
        Assert.Equal(CancellationToken.None, application.StopToken);
    }

    [Fact]
    public async Task StartAsync_WhenStartupAndCleanupFail_PreservesStartupFailure()
    {
        var startupFailure = new InvalidOperationException("startup failed");
        var application = new RecordingApplication
        {
            StartFailure = startupFailure,
            StopFailure = new InvalidOperationException("cleanup failed")
        };
        var hostedService = new BeanBotHostedService(
            application,
            new RecordingHostLifetime(),
            NullLogger<BeanBotHostedService>.Instance);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => hostedService.StartAsync(CancellationToken.None));

        Assert.Same(startupFailure, thrown);
        Assert.Equal(1, application.StopCount);
    }

    [Fact]
    public async Task Host_StopApplicationDuringStartup_CancelsStartupAndCleansUpOnce()
    {
        var application = new RecordingApplication { BlockUntilCancelled = true };
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IBeanBotApplication>(application);
        builder.Services.AddHostedService<BeanBotHostedService>();
        var host = builder.Build();

        try
        {
            var startup = host.StartAsync();
            await application.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
            Assert.True(application.StartToken.IsCancellationRequested);
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposableHost)
            {
                await asyncDisposableHost.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }

        Assert.Equal(1, application.StopCount);
    }

    private sealed class RecordingApplication : IBeanBotApplication
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public CancellationToken StartToken { get; private set; }
        public CancellationToken StopToken { get; private set; }
        public Exception? StartFailure { get; init; }
        public Exception? StopFailure { get; init; }
        public bool BlockUntilCancelled { get; init; }
        public TaskCompletionSource StartEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            StartToken = cancellationToken;
            StartEntered.TrySetResult();
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            StopToken = cancellationToken;
            return StopFailure is null
                ? Task.CompletedTask
                : Task.FromException(StopFailure);
        }
    }

    private sealed class RecordingHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
