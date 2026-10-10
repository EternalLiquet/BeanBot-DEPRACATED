using BeanBot.Health;
using Xunit;

namespace BeanBot.Tests.Health;

public class ApplicationReadinessHostedServiceTests
{
    [Fact]
    public async Task StartAsync_MarksReadyAndStopAsyncBeginsDraining()
    {
        var state = new ApplicationReadinessState();
        var service = new ApplicationReadinessHostedService(state);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(ApplicationLifecycleState.Ready, state.CreateSnapshot().State);

        await service.StopAsync(CancellationToken.None);
        Assert.Equal(ApplicationLifecycleState.Draining, state.CreateSnapshot().State);
    }

    [Fact]
    public async Task StartAsync_PreCanceledToken_DoesNotPublishReady()
    {
        var state = new ApplicationReadinessState();
        var service = new ApplicationReadinessHostedService(state);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.StartAsync(cancellation.Token));

        Assert.Equal(ApplicationLifecycleState.Starting, state.CreateSnapshot().State);
    }

    [Fact]
    public async Task StopAsync_IsIdempotentAndReadinessCannotBeRestored()
    {
        var state = new ApplicationReadinessState();
        var service = new ApplicationReadinessHostedService(state);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        state.MarkReady();

        Assert.Equal(ApplicationLifecycleState.Draining, state.CreateSnapshot().State);
    }
}
