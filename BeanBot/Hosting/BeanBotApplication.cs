using System.Runtime.ExceptionServices;
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
    void StopReactionServices();
    void StopNewMemberEvents();
    void StopEditedMessageEvents();
    void StopCommandServices();
    void StopMessageWaiter();
    void StopPaginator();
    void UnsubscribeDiscordLog();
    Task StopGatewayRecoveryAsync();
    void UnsubscribeApplicationEvents();
    Task StopPunServiceAsync();
    Task StopHealthServerAsync(CancellationToken cancellationToken);
    Task FlushOwnerAlertsAsync();
    Task StopDiscordAsync(CancellationToken cancellationToken);
    void DisposeDiscordClient();
}

internal sealed class BeanBotApplication : IBeanBotApplication
{
    private static readonly TimeSpan DefaultShutdownStageTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PostCancellationStageTimeout = TimeSpan.FromSeconds(1);

    private readonly IBeanBotRuntime _runtime;
    private readonly ILogger<BeanBotApplication> _logger;
    private readonly TimeSpan _shutdownStageTimeout;
    private int _startRequested;
    private int _stopRequested;

    public BeanBotApplication(
        IBeanBotRuntime runtime,
        ILogger<BeanBotApplication> logger,
        TimeSpan? shutdownStageTimeout = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownStageTimeout = shutdownStageTimeout ?? DefaultShutdownStageTimeout;
        if (_shutdownStageTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownStageTimeout));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _startRequested, 1) != 0)
        {
            return;
        }

        BeanBotLog.ApplicationStarting(
            _logger,
            BuildIdentity.Current.Version,
            BuildIdentity.Current.CommitSha);
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

        Exception? firstFailure = null;

        async Task RunStageAsync(
            string stageName,
            Func<Task> stage,
            bool runAfterCancellation = true)
        {
            if (cancellationToken.IsCancellationRequested && !runAfterCancellation)
            {
                BeanBotLog.ShutdownStageSkipped(_logger, stageName);
                return;
            }

            Task? operation = null;
            try
            {
                operation = stage();
                var cancellationAlreadyElapsed = cancellationToken.IsCancellationRequested;
                var stageTimeout = cancellationAlreadyElapsed && runAfterCancellation
                    ? Min(_shutdownStageTimeout, PostCancellationStageTimeout)
                    : _shutdownStageTimeout;
                var waitCancellationToken = cancellationAlreadyElapsed && runAfterCancellation
                    ? CancellationToken.None
                    : cancellationToken;
                await operation.WaitAsync(stageTimeout, waitCancellationToken);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                firstFailure ??= exception;
                BeanBotLog.ShutdownStageCanceled(_logger, stageName);
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
                BeanBotLog.ShutdownStageFailed(_logger, stageName, exception);
            }

            if (operation is { IsCompleted: false })
            {
                _ = operation.ContinueWith(
                    completedTask => BeanBotLog.ShutdownStageLateFailure(
                        _logger,
                        stageName,
                        completedTask.Exception!),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        await RunSynchronousStageAsync("reaction-services", _runtime.StopReactionServices);
        await RunSynchronousStageAsync("new-member-events", _runtime.StopNewMemberEvents);
        await RunSynchronousStageAsync("edited-message-events", _runtime.StopEditedMessageEvents);
        await RunSynchronousStageAsync("command-services", _runtime.StopCommandServices);
        await RunSynchronousStageAsync("message-waiter", _runtime.StopMessageWaiter);
        await RunSynchronousStageAsync("paginator", _runtime.StopPaginator);
        await RunSynchronousStageAsync("discord-log", _runtime.UnsubscribeDiscordLog);
        await RunStageAsync("gateway-recovery", _runtime.StopGatewayRecoveryAsync);
        await RunSynchronousStageAsync("application-events", _runtime.UnsubscribeApplicationEvents);
        await RunStageAsync("pun-service", _runtime.StopPunServiceAsync);
        await RunStageAsync("health-server", () => _runtime.StopHealthServerAsync(cancellationToken));
        await RunStageAsync("owner-alerts-before-discord", _runtime.FlushOwnerAlertsAsync, false);

        var canStopDiscord = false;
        await RunSynchronousStageAsync(
            "discord-startup-state",
            () => canStopDiscord = !_runtime.HasUnfinishedDiscordStartupOperation);
        if (!canStopDiscord)
        {
            BeanBotLog.DiscordStopSkipped(_logger);
        }
        else
        {
            await RunStageAsync(
                "discord-stop",
                () => _runtime.StopDiscordAsync(cancellationToken),
                false);
            await RunSynchronousStageAsync("discord-client-disposal", _runtime.DisposeDiscordClient);
        }

        await RunStageAsync("owner-alerts-final", _runtime.FlushOwnerAlertsAsync, false);

        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }

        Task RunSynchronousStageAsync(string stageName, Action stage)
            => RunStageAsync(stageName, () => Task.Run(stage, CancellationToken.None));

        static TimeSpan Min(TimeSpan left, TimeSpan right)
            => left <= right ? left : right;
    }
}
