using BeanBot.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeanBot.Hosting;

internal interface IBeanBotApplication
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class BeanBotHostedService : IHostedService
{
    private readonly IBeanBotApplication _application;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly ILogger<BeanBotHostedService> _logger;

    public BeanBotHostedService(
        IBeanBotApplication application,
        IHostApplicationLifetime hostLifetime,
        ILogger<BeanBotHostedService> logger)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _hostLifetime = hostLifetime ?? throw new ArgumentNullException(nameof(hostLifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _hostLifetime.ApplicationStopping);
            await _application.StartAsync(startupCancellation.Token);
        }
        catch
        {
            try
            {
                await _application.StopAsync(CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                BeanBotLog.StartupCleanupFailed(_logger, cleanupException);
            }

            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => _application.StopAsync(cancellationToken);
}
