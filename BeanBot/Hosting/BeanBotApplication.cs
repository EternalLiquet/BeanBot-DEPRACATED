using System.Runtime.ExceptionServices;
using BeanBot.Logging;
using Microsoft.Extensions.Logging;

namespace BeanBot.Hosting;

internal interface IBeanBotRuntime
{
    bool HasActiveDiscordLifecycleOperation { get; }
    bool CanDisposeDiscordClient { get; }
    void SubscribeApplicationEvents();
    Task StartHealthServerAsync(CancellationToken cancellationToken);
    Task StartDiscordAsync(CancellationToken cancellationToken);
    Task AcquireInstanceLeaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    void StartGatewayRecovery();
    Task StartCommandServicesAsync();
    void StartEventAndBackgroundServices();
    void StopReactionServices();
    void StopNewMemberEvents();
    Task StopEditedMessageEventsAsync();
    Task<bool> StopCommandServicesAsync();
    Task StopCommandRepliesAsync();
    void StopMessageWaiter();
    void StopPaginator();
    void UnsubscribeDiscordLog();
    Task StopGatewayRecoveryAsync();
    void UnsubscribeApplicationEvents();
    Task StopPunServiceAsync();
    Task ReleaseInstanceLeaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    void SkipInstanceLeaseRelease()
    {
    }
    Task StopHealthServerAsync(CancellationToken cancellationToken);
    Task FlushOwnerAlertsAsync();
    Task StopOwnerAlertsAsync() => Task.CompletedTask;
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
        await _runtime.AcquireInstanceLeaseAsync(cancellationToken);
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
        var commandServicesDrained = false;
        var ownerAlertsDrained = false;
        var ownerAlertsStopped = false;

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
        await RunStageAsync("edited-message-events", _runtime.StopEditedMessageEventsAsync);
        await RunStageAsync(
            "command-services",
            async () =>
            {
                commandServicesDrained = await _runtime.StopCommandServicesAsync();
                if (!commandServicesDrained)
                {
                    throw new TimeoutException(
                        "Legacy command execution did not drain before the command-services shutdown bound.");
                }
            });
        await RunStageAsync("command-replies", _runtime.StopCommandRepliesAsync);
        await RunSynchronousStageAsync("message-waiter", _runtime.StopMessageWaiter);
        await RunSynchronousStageAsync("paginator", _runtime.StopPaginator);
        await RunSynchronousStageAsync("discord-log", _runtime.UnsubscribeDiscordLog);
        await RunStageAsync("gateway-recovery", _runtime.StopGatewayRecoveryAsync);
        await RunSynchronousStageAsync("application-events", _runtime.UnsubscribeApplicationEvents);
        await RunStageAsync("pun-service", _runtime.StopPunServiceAsync);
        await RunStageAsync(
            "owner-alerts-before-lease-release",
            async () =>
            {
                await _runtime.FlushOwnerAlertsAsync();
                ownerAlertsDrained = true;
            },
            false);
        await RunStageAsync(
            "owner-alert-admission",
            async () =>
            {
                await _runtime.StopOwnerAlertsAsync();
                ownerAlertsStopped = true;
            },
            false);

        var canReleaseInstanceLease = false;
        await RunSynchronousStageAsync(
            "instance-lease-release-state",
            () => canReleaseInstanceLease = commandServicesDrained &&
                ownerAlertsDrained &&
                ownerAlertsStopped &&
                !_runtime.HasActiveDiscordLifecycleOperation);
        if (canReleaseInstanceLease)
        {
            await RunStageAsync(
                "instance-lease-release",
                () => _runtime.ReleaseInstanceLeaseAsync(CancellationToken.None));
        }
        else
        {
            await RunSynchronousStageAsync("instance-lease-release-skipped", _runtime.SkipInstanceLeaseRelease);
        }

        await RunStageAsync("health-server", StopHealthServerAsync);

        var canStopDiscord = false;
        await RunSynchronousStageAsync(
            "discord-startup-state",
            () => canStopDiscord = commandServicesDrained &&
                !_runtime.HasActiveDiscordLifecycleOperation);
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
            if (_runtime.CanDisposeDiscordClient)
            {
                await RunSynchronousStageAsync("discord-client-disposal", _runtime.DisposeDiscordClient);
            }
            else
            {
                BeanBotLog.DiscordDisposalSkipped(_logger);
            }
        }

        await RunStageAsync("owner-alerts-final", _runtime.FlushOwnerAlertsAsync, false);

        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }

        Task RunSynchronousStageAsync(string stageName, Action stage)
            => RunStageAsync(stageName, () => Task.Run(stage, CancellationToken.None));

        Task StopHealthServerAsync()
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                return _runtime.StopHealthServerAsync(cancellationToken);
            }

            return StopHealthWithFreshTokenAsync();
        }

        async Task StopHealthWithFreshTokenAsync()
        {
            using var healthCleanup = new CancellationTokenSource(
                Min(_shutdownStageTimeout, PostCancellationStageTimeout));
            await _runtime.StopHealthServerAsync(healthCleanup.Token);
        }

        static TimeSpan Min(TimeSpan left, TimeSpan right)
            => left <= right ? left : right;
    }
}
