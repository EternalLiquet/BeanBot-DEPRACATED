using BeanBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

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
        var outageStore = new FakeOutageStore();
        await using var recovery = CreateService(state, lifecycle, delay, exitCodes, outageStore);

        Assert.True(recovery.StartMonitoring());
        await delay.WaitForRequestCountAsync(1);

        state.MarkHealthy();
        recovery.NotifyReady();
        await recovery.WaitForIdleAsync();

        Assert.Equal(0, lifecycle.ReconnectCount);
        Assert.Empty(exitCodes);
        Assert.Null(outageStore.CurrentOutage);
    }

    [Fact]
    public async Task RepeatedDisconnects_StartOnlyOneRecoveryMonitor()
    {
        var state = new FakeHealthState();
        var lifecycle = new FakeLifecycle();
        var delay = new ControlledDelay();
        var exitCodes = new List<int>();
        var outageStore = new FakeOutageStore();
        await using var recovery = CreateService(state, lifecycle, delay, exitCodes, outageStore);

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
        var outageStore = new FakeOutageStore();
        await using var recovery = CreateService(state, lifecycle, delay, exitCodes, outageStore);

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
        Assert.True(outageStore.CurrentOutage?.ManualRecoveryAttempted);
        Assert.False(outageStore.CurrentOutage?.ProcessRestartRequested);
    }

    [Fact]
    public async Task FailedRecovery_RequestsProcessRestart()
    {
        var state = new FakeHealthState();
        var lifecycle = new FakeLifecycle();
        var delay = new ControlledDelay();
        var exitCodes = new List<int>();
        var outageStore = new FakeOutageStore();
        await using var recovery = CreateService(state, lifecycle, delay, exitCodes, outageStore);

        recovery.StartMonitoring();
        await delay.WaitForRequestCountAsync(1);
        delay.CompleteNext();
        await lifecycle.ReconnectCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await delay.WaitForRequestCountAsync(1);
        delay.CompleteNext();
        await recovery.WaitForIdleAsync();

        Assert.Equal(1, lifecycle.ReconnectCount);
        Assert.Equal(new[] { 1 }, exitCodes);
        Assert.True(outageStore.CurrentOutage?.ManualRecoveryAttempted);
        Assert.True(outageStore.CurrentOutage?.ProcessRestartRequested);
        Assert.Equal(new[] { "manual", "restart", "exit" }, outageStore.StateTransitions);
    }

    [Fact]
    public async Task ApplicationShutdown_DoesNotRequestProcessRestart()
    {
        var state = new FakeHealthState();
        var lifecycle = new FakeLifecycle();
        var delay = new ControlledDelay();
        var exitCodes = new List<int>();
        var outageStore = new FakeOutageStore();
        var recovery = CreateService(state, lifecycle, delay, exitCodes, outageStore);

        recovery.StartMonitoring();
        await delay.WaitForRequestCountAsync(1);
        await recovery.DisposeAsync();

        Assert.Equal(0, lifecycle.ReconnectCount);
        Assert.Empty(exitCodes);
        Assert.Null(outageStore.CurrentOutage);
    }

    private static DiscordGatewayRecoveryService CreateService(
        FakeHealthState state,
        FakeLifecycle lifecycle,
        ControlledDelay delay,
        List<int> exitCodes,
        FakeOutageStore outageStore)
        => new(
            state.CreateSnapshot,
            lifecycle,
            outageStore,
            NullLogger<DiscordGatewayRecoveryService>.Instance,
            TestOptions,
            delay,
            exitCode =>
            {
                outageStore.StateTransitions.Add("exit");
                exitCodes.Add(exitCode);
            });

    private sealed class FakeOutageStore : IDiscordOutageStore
    {
        public DiscordOutage? CurrentOutage { get; private set; }
        public List<string> StateTransitions { get; } = new();

        public Task<DiscordOutage?> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentOutage);

        public Task OpenAsync(
            DateTimeOffset disconnectedAtUtc,
            string? mostRecentDisconnectReason,
            CancellationToken cancellationToken = default)
        {
            CurrentOutage ??= CreateOutage(disconnectedAtUtc, mostRecentDisconnectReason);
            return Task.CompletedTask;
        }

        public Task MarkManualRecoveryAttemptedAsync(
            DateTimeOffset disconnectedAtUtc,
            string? mostRecentDisconnectReason,
            CancellationToken cancellationToken = default)
        {
            CurrentOutage ??= CreateOutage(disconnectedAtUtc, mostRecentDisconnectReason);
            CurrentOutage.MostRecentDisconnectReason = NormalizeReason(mostRecentDisconnectReason);
            CurrentOutage.ManualRecoveryAttempted = true;
            StateTransitions.Add("manual");
            return Task.CompletedTask;
        }

        public Task MarkProcessRestartRequestedAsync(
            DateTimeOffset disconnectedAtUtc,
            string? mostRecentDisconnectReason,
            CancellationToken cancellationToken = default)
        {
            Assert.NotNull(CurrentOutage);
            CurrentOutage.MostRecentDisconnectReason = NormalizeReason(mostRecentDisconnectReason);
            CurrentOutage.ProcessRestartRequested = true;
            StateTransitions.Add("restart");
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            CurrentOutage = null;
            return Task.CompletedTask;
        }

        private static DiscordOutage CreateOutage(
            DateTimeOffset disconnectedAtUtc,
            string? mostRecentDisconnectReason)
            => new()
            {
                DisconnectedAtUtc = disconnectedAtUtc,
                MostRecentDisconnectReason = NormalizeReason(mostRecentDisconnectReason)
            };

        private static string NormalizeReason(string? reason)
            => reason ?? "Discord gateway disconnected.";
    }

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
