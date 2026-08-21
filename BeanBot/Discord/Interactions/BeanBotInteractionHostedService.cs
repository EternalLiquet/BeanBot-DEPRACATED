using Microsoft.Extensions.Hosting;

namespace BeanBot.Discord.Interactions;

internal sealed class BeanBotInteractionHostedService : IHostedService
{
    private readonly InteractionHandler _interactionHandler;

    public BeanBotInteractionHostedService(InteractionHandler interactionHandler)
    {
        _interactionHandler = interactionHandler ?? throw new ArgumentNullException(nameof(interactionHandler));
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => _interactionHandler.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _interactionHandler.Dispose();
        return Task.CompletedTask;
    }
}
