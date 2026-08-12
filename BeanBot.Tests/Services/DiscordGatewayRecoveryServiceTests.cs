using BeanBot.Services;

using System.Collections.Concurrent;

using Xunit;

namespace BeanBot.Tests.Services;

public class DiscordGatewayRecoveryServiceTests
{
    private static readonly DiscordGatewayRecoveryOptions TestOptions = new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2));

    [Fact]
    public async Task TemporaryDisconnect_ReachesReadyBeforeManualRecovery()
    {
        var state = new FakeHealthState();
        var lifecycle = new FakeLifecycle();
        var delay = new ControlledDelay();
        var exitCodes = new List<int>();
        await using var recovery = CreateService(state, lifecycle, delay, exitCodes);

        Assert.True(recovery.StartMonitoring());
        await delay.WaitForRequestCountAsync(1);

        state.MarkHealthy();
        recovery.NotifyReady();
        await recovery.WaitForIdleAsync();

        Assert.Equal(0, lifecycle.ReconnectCount);
        Assert.Empty(exitCodes);
    }

    [Fact]
    public async Task RepeatedDisconnects_StartOnlyOneRecoveryMonitor()
    {
        var state = new FakeHealthState();
        var lifecycle = new FakeLifecycle();
        var delay = new ControlledDelay();
        var exitCodes = new List<int>();
        await using var recovery = CreateService(state, lifecycle, delay, exitCodes);

        Assert.True(recovery.StartMonitoring());
        Assert.False(recovery.StartMonitoring());
        Assert.False(recovery.StartMonitoring());

        await delay.WaitForRequestCountAsync(1);
        Assert.Equal(1, delay.RequestCount);
        Assert.Equal(0, lifecycle.ReconnectCount);
    }

    [Fact]
    public async Task ReadyAfterManualReconnect_CancelsFailureEscalation()
    {
        var state = new FakeHealthState();
        var lifecycle = new FakeLifecycle();
        var delay = new ControlledDelay();
        var exitCodes = new List<int>();
        await using var recovery = CreateService(state, lifecycle, delay, exitCodes);

        recovery.StartMonitoring();
        await delay.WaitForRequestCountAsync(1);
        delay.CompleteNext();
        await lifecycle.ReconnectCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await delay.WaitForRequestCountAsync(1);

        state.MarkHealthy();
        recovery.NotifyReady();
        await recovery.WaitForIdleAsync();

        Assert.Equal(1, lifecycle.ReconnectCount);
        Assert.Empty(exitCodes);
    }

    [Fact]
    public async Task FailedRecovery_RequestsProcessRestart()
    {
        var state = new FakeHealthState();
        var lifecycle = new FakeLifecycle();
        var delay = new ControlledDelay();
        var exitCodes = new List<int>();
        await using var recovery = CreateService(state, lifecycle, delay, exitCodes);

        recovery.StartMonitoring();
        await delay.WaitForRequestCountAsync(1);
        delay.CompleteNext();
        await lifecycle.ReconnectCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await delay.WaitForRequestCountAsync(1);
        delay.CompleteNext();
        await recovery.WaitForIdleAsync();

        Assert.Equal(1, lifecycle.ReconnectCount);
        Assert.Equal(new[] { 1 }, exitCodes);
    }

    [Fact]
    public async Task ApplicationShutdown_DoesNotRequestProcessRestart()
    {
        var state = new FakeHealthState();
        var lifecycle = new FakeLifecycle();
        var delay = new ControlledDelay();
        var exitCodes = new List<int>();
        var recovery = CreateService(state, lifecycle, delay, exitCodes);

        recovery.StartMonitoring();
        await delay.WaitForRequestCountAsync(1);
        await recovery.DisposeAsync();

        Assert.Equal(0, lifecycle.ReconnectCount);
        Assert.Empty(exitCodes);
    }

    private static DiscordGatewayRecoveryService CreateService(
        FakeHealthState state,
        FakeLifecycle lifecycle,
        ControlledDelay delay,
        List<int> exitCodes)
        => new(
            state.CreateSnapshot,
            lifecycle,
            TestOptions,
            delay,
            exitCodes.Add);

    private sealed class FakeHealthState
    {
        private bool _healthy;
        private readonly DateTimeOffset _unhealthySince = DateTimeOffset.UtcNow;

        public void MarkHealthy() => _healthy = true;

        public DiscordHealthSnapshot CreateSnapshot()
            => new(
                _healthy,
                _healthy ? "Connected" : "Disconnected",
                "LoggedIn",
                _healthy ? "Connected" : "Disconnected",
                _healthy ? DateTimeOffset.UtcNow : null,
                _healthy ? null : _unhealthySince,
                _healthy ? null : _unhealthySince,
                _healthy ? null : "Test disconnect");
    }

    private sealed class FakeLifecycle : IDiscordGatewayLifecycle
    {
        private int _reconnectCount;

        public int ReconnectCount => Volatile.Read(ref _reconnectCount);
        public TaskCompletionSource<bool> ReconnectCalled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReconnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reconnectCount);
            ReconnectCalled.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class ControlledDelay : IRecoveryDelay
    {
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _requests = new();

        public int RequestCount => _requests.Count;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _ = completion.Task.ContinueWith(
                _ => registration.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _requests.Enqueue(completion);
            return completion.Task;
        }

        public void CompleteNext()
        {
            Assert.True(_requests.TryDequeue(out var completion));
            completion.TrySetResult(true);
        }

        public async Task WaitForRequestCountAsync(int expectedCount)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (_requests.Count < expectedCount && DateTime.UtcNow < deadline)
            {
                await Task.Yield();
            }

            Assert.True(_requests.Count >= expectedCount, "The recovery delay was not requested in time.");
        }
    }
}
