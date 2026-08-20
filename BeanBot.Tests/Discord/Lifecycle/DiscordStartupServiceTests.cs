using System.Net;
using BeanBot.Discord.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Lifecycle;

public class DiscordStartupServiceTests
{
    [Fact]
    public async Task SuccessfulStartup_LogsInAndStartsOnceWithoutDelay()
    {
        var lifecycle = new FakeStartupLifecycle();
        var delay = new RecordingDelay();
        var service = CreateService(lifecycle, DiscordStartupOptions.Default, delay);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, lifecycle.LoginCount);
        Assert.Equal(1, lifecycle.StartCount);
        Assert.Equal(1, lifecycle.PresenceCount);
        Assert.Empty(delay.RequestedDelays);
    }

    [Fact]
    public async Task TransientLoginFailure_DelaysAndRetries()
    {
        var lifecycle = new FakeStartupLifecycle(
            CreateHttpException(HttpStatusCode.ServiceUnavailable),
            null);
        var delay = new RecordingDelay();
        var service = CreateService(lifecycle, DiscordStartupOptions.Default, delay);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(2, lifecycle.LoginCount);
        Assert.Equal(1, lifecycle.StartCount);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, delay.RequestedDelays);
    }

    [Fact]
    public async Task CancellationBeforeStartup_DoesNotAttemptLogin()
    {
        var lifecycle = new FakeStartupLifecycle();
        var service = CreateService(lifecycle, DiscordStartupOptions.Default, new RecordingDelay());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.StartAsync(cancellation.Token));

        Assert.Equal(0, lifecycle.LoginCount);
        Assert.Equal(0, lifecycle.StartCount);
    }

    [Fact]
    public async Task CancellationDuringRetryDelay_PreventsAnotherAttempt()
    {
        var lifecycle = new FakeStartupLifecycle(
            CreateHttpException(HttpStatusCode.ServiceUnavailable));
        var delay = new BlockingDelay();
        var service = CreateService(lifecycle, DiscordStartupOptions.Default, delay);
        using var cancellation = new CancellationTokenSource();

        var startup = service.StartAsync(cancellation.Token);
        await delay.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Equal(1, lifecycle.LoginCount);
        Assert.Equal(0, lifecycle.StartCount);
    }

    [Fact]
    public async Task UnauthorizedLoginFailure_FailsWithoutRetry()
    {
        var unauthorized = CreateHttpException(HttpStatusCode.Unauthorized);
        var lifecycle = new FakeStartupLifecycle(unauthorized);
        var delay = new RecordingDelay();
        var service = CreateService(lifecycle, DiscordStartupOptions.Default, delay);

        var thrown = await Assert.ThrowsAsync<global::Discord.Net.HttpException>(
            () => service.StartAsync(CancellationToken.None));

        Assert.Same(unauthorized, thrown);
        Assert.Equal(1, lifecycle.LoginCount);
        Assert.Equal(0, lifecycle.StartCount);
        Assert.Empty(delay.RequestedDelays);
    }

    [Fact]
    public async Task TransientLoginFailures_ExhaustConfiguredAttempts()
    {
        var finalFailure = CreateHttpException(HttpStatusCode.BadGateway);
        var lifecycle = new FakeStartupLifecycle(
            CreateHttpException(HttpStatusCode.ServiceUnavailable),
            CreateHttpException(HttpStatusCode.TooManyRequests),
            finalFailure);
        var delay = new RecordingDelay();
        var service = CreateService(lifecycle, DiscordStartupOptions.Default, delay);

        var thrown = await Assert.ThrowsAsync<global::Discord.Net.HttpException>(
            () => service.StartAsync(CancellationToken.None));

        Assert.Same(finalFailure, thrown);
        Assert.Equal(3, lifecycle.LoginCount);
        Assert.Equal(0, lifecycle.StartCount);
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) },
            delay.RequestedDelays);
    }

    [Fact]
    public async Task LoginTimeout_IsTerminalAndDoesNotRetry()
    {
        var timeout = new TimeoutException("Test timeout");
        var lifecycle = new FakeStartupLifecycle(timeout);
        var delay = new RecordingDelay();
        var service = CreateService(lifecycle, DiscordStartupOptions.Default, delay);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => service.StartAsync(CancellationToken.None));

        Assert.Same(timeout, thrown);
        Assert.Equal(1, lifecycle.LoginCount);
        Assert.Equal(0, lifecycle.StartCount);
        Assert.Empty(delay.RequestedDelays);
    }

    [Fact]
    public async Task InFlightLoginCancellation_ReturnsPromptlyAndBlocksCompetingLifecycleOperations()
    {
        var login = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        var lifecycle = new DiscordStartupLifecycle(
            () => login.Task,
            () =>
            {
                startCount++;
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            TimeSpan.FromMinutes(1),
            NullLogger<DiscordStartupLifecycle>.Instance);
        var service = CreateService(
            lifecycle,
            DiscordStartupOptions.Default,
            new RecordingDelay());
        using var cancellation = new CancellationTokenSource();

        var startup = service.StartAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => startup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(lifecycle.HasUnfinishedOperation);
        Assert.Equal(0, startCount);

        login.TrySetResult(true);
        await WaitForNoUnfinishedOperationAsync(lifecycle);
    }

    [Fact]
    public async Task ProductionLifecycleTimeout_TracksOperationAndObservesLateFailure()
    {
        var login = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFailure = new TaskCompletionSource<(Exception Exception, string Operation)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new DiscordStartupLifecycle(
            () => login.Task,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            TimeSpan.FromMilliseconds(20),
            NullLogger<DiscordStartupLifecycle>.Instance,
            (exception, operation) => lateFailure.TrySetResult((exception, operation)));

        await Assert.ThrowsAsync<TimeoutException>(
            () => lifecycle.LoginAsync(CancellationToken.None));
        Assert.True(lifecycle.HasUnfinishedOperation);

        var expectedFailure = new InvalidOperationException("Late test failure");
        login.TrySetException(expectedFailure);
        var observed = await lateFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("login", observed.Operation);
        Assert.Same(expectedFailure, observed.Exception.InnerException);
        await WaitForNoUnfinishedOperationAsync(lifecycle);
    }

    [Fact]
    public async Task PresenceFailure_DoesNotFailConnectedStartup()
    {
        var lifecycle = new FakeStartupLifecycle
        {
            PresenceFailure = new InvalidOperationException("Test presence failure")
        };
        var service = CreateService(lifecycle, DiscordStartupOptions.Default, new RecordingDelay());

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, lifecycle.LoginCount);
        Assert.Equal(1, lifecycle.StartCount);
        Assert.Equal(1, lifecycle.PresenceCount);
    }

    [Fact]
    public async Task PresenceTimeout_WithUnfinishedOperation_IsTerminal()
    {
        var presence = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new DiscordStartupLifecycle(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => presence.Task,
            TimeSpan.FromMilliseconds(20),
            NullLogger<DiscordStartupLifecycle>.Instance);
        var service = CreateService(
            lifecycle,
            DiscordStartupOptions.Default,
            new RecordingDelay());

        await Assert.ThrowsAsync<TimeoutException>(
            () => service.StartAsync(CancellationToken.None));
        Assert.True(lifecycle.HasUnfinishedOperation);

        presence.TrySetResult(true);
        await WaitForNoUnfinishedOperationAsync(lifecycle);
    }

    [Fact]
    public async Task CancellationDuringPresence_PropagatesAndTracksUnfinishedOperation()
    {
        var presenceStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presence = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new DiscordStartupLifecycle(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () =>
            {
                presenceStarted.TrySetResult(true);
                return presence.Task;
            },
            TimeSpan.FromMinutes(1),
            NullLogger<DiscordStartupLifecycle>.Instance);
        var service = CreateService(
            lifecycle,
            DiscordStartupOptions.Default,
            new RecordingDelay());
        using var cancellation = new CancellationTokenSource();

        var startup = service.StartAsync(cancellation.Token);
        await presenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => startup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(lifecycle.HasUnfinishedOperation);

        presence.TrySetResult(true);
        await WaitForNoUnfinishedOperationAsync(lifecycle);
    }

    private static DiscordStartupService CreateService(
        IDiscordStartupLifecycle lifecycle,
        DiscordStartupOptions options,
        IDiscordStartupDelay delay)
        => new(
            lifecycle,
            NullLogger<DiscordStartupService>.Instance,
            options,
            delay);

    private static global::Discord.Net.HttpException CreateHttpException(HttpStatusCode statusCode)
        => new(statusCode, null, null, "Test Discord failure", null);

    private static async Task WaitForNoUnfinishedOperationAsync(DiscordStartupLifecycle lifecycle)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (lifecycle.HasUnfinishedOperation && DateTime.UtcNow < deadline)
        {
            await Task.Yield();
        }

        Assert.False(lifecycle.HasUnfinishedOperation);
    }

    private sealed class FakeStartupLifecycle : IDiscordStartupLifecycle
    {
        private readonly Queue<Exception?> _loginFailures;

        public FakeStartupLifecycle(params Exception?[] loginFailures)
        {
            _loginFailures = new Queue<Exception?>(loginFailures);
        }

        public int LoginCount { get; private set; }
        public int StartCount { get; private set; }
        public int PresenceCount { get; private set; }
        public Exception? PresenceFailure { get; set; }

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
            PresenceCount++;
            return PresenceFailure is null
                ? Task.CompletedTask
                : Task.FromException(PresenceFailure);
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
        public TaskCompletionSource<bool> Requested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Requested.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
