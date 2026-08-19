using BeanBot.Logging;
using Microsoft.Extensions.Logging;

namespace BeanBot.Hosting;

internal interface IBeanBotRuntime
{
    bool HasUnfinishedDiscordStartupOperation { get; }
    void SubscribeApplicationEvents();
    Task StartHealthServerAsync(CancellationToken cancellationToken);
    Task StartDiscordAsync(CancellationToken cancellationToken);
    void StartGatewayRecovery();
    Task StartCommandServicesAsync();
    void StartEventAndBackgroundServices();
    void StopEventAndCommandServices();
    Task StopGatewayRecoveryAsync();
    void UnsubscribeApplicationEvents();
    Task StopBackgroundServicesAsync(CancellationToken cancellationToken);
    Task FlushOwnerAlertsAsync();
    Task StopDiscordAsync(CancellationToken cancellationToken);
    void DisposeDiscordClient();
}

internal sealed class BeanBotApplication : IBeanBotApplication
{
    private readonly IBeanBotRuntime _runtime;
    private readonly ILogger<BeanBotApplication> _logger;
    private int _startRequested;
    private int _stopRequested;

    public BeanBotApplication(
        IBeanBotRuntime runtime,
        ILogger<BeanBotApplication> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _startRequested, 1) != 0)
        {
            return;
        }

        _runtime.SubscribeApplicationEvents();
        await _runtime.StartHealthServerAsync(cancellationToken);
        await _runtime.StartDiscordAsync(cancellationToken);
        _runtime.StartGatewayRecovery();
        await _runtime.StartCommandServicesAsync();
        _runtime.StartEventAndBackgroundServices();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        _runtime.StopEventAndCommandServices();
        await _runtime.StopGatewayRecoveryAsync();
        _runtime.UnsubscribeApplicationEvents();
        await _runtime.StopBackgroundServicesAsync(cancellationToken);

        await _runtime.FlushOwnerAlertsAsync();
        if (_runtime.HasUnfinishedDiscordStartupOperation)
        {
            BeanBotLog.DiscordStopSkipped(_logger);
        }
        else
        {
            await _runtime.StopDiscordAsync(cancellationToken);
            _runtime.DisposeDiscordClient();
        }

        await _runtime.FlushOwnerAlertsAsync();
    }
}
