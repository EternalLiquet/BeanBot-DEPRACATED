using BeanBot.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunLeaseOwnedOperationAsync_RequiresConfirmedLease(bool isHeld)
    {
        var operationCount = 0;
        var executed = await BeanBotRuntime.RunLeaseOwnedOperationAsync(
            new StubInstanceLeaseHealth(isHeld),
            () =>
            {
                operationCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(isHeld, executed);
        Assert.Equal(isHeld ? 1 : 0, operationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunLeaseOwnedOperation_RequiresConfirmedLease(bool isHeld)
    {
        var operationCount = 0;
        var executed = BeanBotRuntime.RunLeaseOwnedOperation(
            new StubInstanceLeaseHealth(isHeld),
            () =>
            {
                operationCount++;
                return true;
            });

        Assert.Equal(isHeld, executed);
        Assert.Equal(isHeld ? 1 : 0, operationCount);
    }

    [Fact]
    public async Task RunBoundedShutdownOperationAsync_OperationFailurePropagates()
    {
        var failure = new InvalidOperationException("injected Discord shutdown failure");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BeanBotRuntime.RunBoundedShutdownOperationAsync(
                () => Task.FromException(failure),
                "stop",
                TimeSpan.FromSeconds(1),
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task RunBoundedShutdownOperationAsync_PreCanceledTokenSkipsOperationAndPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var operationStarted = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BeanBotRuntime.RunBoundedShutdownOperationAsync(
                () =>
                {
                    operationStarted = true;
                    return Task.CompletedTask;
                },
                "stop",
                TimeSpan.FromSeconds(1),
                NullLogger.Instance,
                cancellation.Token));

        Assert.False(operationStarted);
    }

    [Fact]
    public async Task RunBoundedShutdownOperationAsync_TimeoutPropagatesAndLateFaultIsObserved()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(
            () => BeanBotRuntime.RunBoundedShutdownOperationAsync(
                () => operation.Task,
                "stop",
                TimeSpan.FromMilliseconds(20),
                NullLogger.Instance,
                CancellationToken.None));

        operation.SetException(new InvalidOperationException("late failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Task);
    }

    private sealed class StubInstanceLeaseHealth(bool isHeld) : IInstanceLeaseHealth
    {
        public bool IsHeld { get; } = isHeld;
    }
}
