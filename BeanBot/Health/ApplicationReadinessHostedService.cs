using Microsoft.Extensions.Hosting;

namespace BeanBot.Health;

internal sealed class ApplicationReadinessHostedService : IHostedService
{
    private readonly ApplicationReadinessState _applicationReadinessState;

    public ApplicationReadinessHostedService(ApplicationReadinessState applicationReadinessState)
    {
        _applicationReadinessState = applicationReadinessState ??
            throw new ArgumentNullException(nameof(applicationReadinessState));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _applicationReadinessState.MarkReady();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _applicationReadinessState.BeginDraining();
        return Task.CompletedTask;
    }
}
