using BeanBot.Logging;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

public sealed class EditMessageHandler : IDisposable
{
    internal const int MaximumInFlightOperations = 64;
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly DiscordSocketClient _discordClient;
    private readonly EditMessageEventServices _editMessageEventService;
    private readonly ILogger<EditMessageHandler> _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _inFlightOperations = [];
    private readonly HashSet<Task> _ownedDiscordOperations = [];
    private bool _initialized;
    private bool _stopping;

    public EditMessageHandler(
        DiscordSocketClient discordClient,
        EditMessageEventServices editMessageEventService,
        ILogger<EditMessageHandler> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _editMessageEventService = editMessageEventService ?? throw new ArgumentNullException(nameof(editMessageEventService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal bool HasInFlightOperations
    {
        get
        {
            lock (_sync)
            {
                return _inFlightOperations.Count != 0 || _ownedDiscordOperations.Count != 0;
            }
        }
    }

    public void InitializeEventListener()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);

            if (_initialized)
            {
                return;
            }

            _discordClient.MessageUpdated += HandleUpdateAsync;
            _initialized = true;
        }
    }

    internal bool TryTrackOperation(Func<CancellationToken, Task> operationFactory)
    {
        ArgumentNullException.ThrowIfNull(operationFactory);

        lock (_sync)
        {
            if (_stopping)
            {
                return false;
            }

            if (_inFlightOperations.Count >= MaximumInFlightOperations)
            {
                EditMessageLog.AdmissionCapacityExceeded(_logger, MaximumInFlightOperations);
                return false;
            }

            Task operation;
            try
            {
                operation = operationFactory(_lifetimeCancellation.Token);
            }
            catch (Exception exception)
            {
                EditMessageLog.OperationFailed(_logger, exception);
                return true;
            }

            _inFlightOperations.Add(operation);
            ObserveTrackedOperation(operation, _inFlightOperations, lateDiscordOperation: false);
            return true;
        }
    }

    public async Task StopAsync(TimeSpan? drainTimeout = null)
    {
        var timeout = drainTimeout ?? DefaultDrainTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout), "Drain timeout must be greater than zero.");
        }

        StopAdmissionAndCancel();

        using var drainCancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            Task[] snapshot;
            lock (_sync)
            {
                snapshot = [.. _inFlightOperations, .. _ownedDiscordOperations];
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(snapshot.Select(SettleAsync))
                    .WaitAsync(drainCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
            {
                int remainingCount;
                lock (_sync)
                {
                    remainingCount = _inFlightOperations
                        .Concat(_ownedDiscordOperations)
                        .Distinct()
                        .Count();
                }

                EditMessageLog.DrainTimedOut(_logger, remainingCount, timeout);
                throw new TimeoutException(
                    $"Timed out draining {remainingCount} edited-message operation(s) after {timeout}.");
            }
        }
    }

    public void Dispose() => StopAdmissionAndCancel();

    private Task HandleUpdateAsync(
        Cacheable<IMessage, ulong> oldMessage,
        SocketMessage newMessage,
        ISocketMessageChannel messageChannel)
    {
        _ = TryTrackOperation(
            token => _editMessageEventService.HandleUpdate(
                oldMessage,
                newMessage,
                messageChannel,
                token,
                TrackOwnedDiscordOperation));
        return Task.CompletedTask;
    }

    internal void TrackOwnedDiscordOperation(Task operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_sync)
        {
            if (operation.IsCompleted)
            {
                ObserveCompletedFault(operation, lateDiscordOperation: true);
                return;
            }

            _ownedDiscordOperations.Add(operation);
            ObserveTrackedOperation(operation, _ownedDiscordOperations, lateDiscordOperation: true);
        }
    }

    private void ObserveTrackedOperation(
        Task operation,
        HashSet<Task> owner,
        bool lateDiscordOperation)
    {
        _ = operation.ContinueWith(
            completedTask =>
            {
                lock (_sync)
                {
                    owner.Remove(completedTask);
                }

                ObserveCompletedFault(completedTask, lateDiscordOperation);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveCompletedFault(Task operation, bool lateDiscordOperation)
    {
        if (!operation.IsFaulted || operation.Exception is null)
        {
            return;
        }

        var exception = operation.Exception.Flatten();
        if (lateDiscordOperation)
        {
            EditMessageLog.LateDiscordOperationFailed(_logger, exception);
        }
        else
        {
            EditMessageLog.OperationFailed(_logger, exception);
        }
    }

    private void StopAdmissionAndCancel()
    {
        var cancelLifetime = false;
        lock (_sync)
        {
            if (!_stopping)
            {
                _stopping = true;
                cancelLifetime = true;
            }

            if (_initialized)
            {
                _discordClient.MessageUpdated -= HandleUpdateAsync;
                _initialized = false;
            }
        }

        if (cancelLifetime)
        {
            _lifetimeCancellation.Cancel();
        }
    }

    private static async Task SettleAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            // Completion is observed by the tracked-operation continuation.
        }
    }
}
