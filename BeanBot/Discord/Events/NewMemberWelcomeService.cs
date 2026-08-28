using System.Collections.Concurrent;
using System.Threading.Channels;
using BeanBot.Configuration;
using BeanBot.Logging;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

internal sealed class NewMemberWelcomeService : IAsyncDisposable
{
    private readonly INewMemberWelcomeDelivery _delivery;
    private readonly NewMemberWelcomeOptions _welcomeOptions;
    private readonly NewMemberWelcomeRuntimeOptions _runtimeOptions;
    private readonly ILogger<NewMemberWelcomeService> _logger;
    private readonly Channel<ulong> _queue;
    private readonly ConcurrentDictionary<ulong, byte> _outstandingUsers = new();
    private readonly SemaphoreSlim _outstandingSlots;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly object _stopLock = new();
    private Task[] _workers = [];
    private Task? _stopTask;
    private int _started;
    private int _accepting;

    public NewMemberWelcomeService(
        INewMemberWelcomeDelivery delivery,
        NewMemberWelcomeOptions welcomeOptions,
        NewMemberWelcomeRuntimeOptions runtimeOptions,
        ILogger<NewMemberWelcomeService> logger)
    {
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _welcomeOptions = welcomeOptions ?? throw new ArgumentNullException(nameof(welcomeOptions));
        _runtimeOptions = runtimeOptions ?? throw new ArgumentNullException(nameof(runtimeOptions));
        _runtimeOptions.Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _queue = Channel.CreateBounded<ulong>(new BoundedChannelOptions(_runtimeOptions.MaximumOutstanding)
        {
            SingleWriter = false,
            SingleReader = _runtimeOptions.WorkerCount == 1,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _outstandingSlots = new SemaphoreSlim(
            _runtimeOptions.MaximumOutstanding,
            _runtimeOptions.MaximumOutstanding);
    }

    internal bool HasActiveDiscordOperation => _delivery.HasActiveOperation;

    internal int OutstandingCount => _outstandingUsers.Count;

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        if (!_welcomeOptions.Enabled)
        {
            _queue.Writer.TryComplete();
            return;
        }

        Volatile.Write(ref _accepting, 1);
        _workers = Enumerable.Range(0, _runtimeOptions.WorkerCount)
            .Select(_ => Task.Run(() => ProcessQueueAsync(_shutdownCancellation.Token)))
            .ToArray();
    }

    internal bool TryEnqueue(ulong userId, bool isBot)
    {
        if (isBot || !_welcomeOptions.Enabled || Volatile.Read(ref _accepting) == 0)
        {
            return false;
        }

        if (!_outstandingSlots.Wait(0))
        {
            BeanBotLog.WelcomeCapacityFull(_logger, userId, _runtimeOptions.MaximumOutstanding);
            return false;
        }

        if (!_outstandingUsers.TryAdd(userId, 0))
        {
            _outstandingSlots.Release();
            BeanBotLog.WelcomeDuplicateSuppressed(_logger, userId);
            return false;
        }

        if (_queue.Writer.TryWrite(userId))
        {
            return true;
        }

        ReleaseOutstanding(userId);
        return false;
    }

    internal void StopAccepting()
    {
        if (Interlocked.Exchange(ref _accepting, 0) == 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
    }

    internal Task StopAsync()
    {
        lock (_stopLock)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    public async ValueTask DisposeAsync()
        => await StopAsync();

    private async Task StopCoreAsync()
    {
        StopAccepting();
        if (_workers.Length == 0)
        {
            DrainPendingQueue();
            return;
        }

        var workers = Task.WhenAll(_workers);
        try
        {
            await workers.WaitAsync(_runtimeOptions.ShutdownDrainTimeout);
        }
        catch (TimeoutException)
        {
            BeanBotLog.WelcomeDrainTimedOut(_logger, _runtimeOptions.ShutdownDrainTimeout);
            _shutdownCancellation.Cancel();
            try
            {
                await workers.WaitAsync(_runtimeOptions.ShutdownCancellationGrace);
            }
            catch (TimeoutException)
            {
                BeanBotLog.WelcomeCancellationGraceTimedOut(
                    _logger,
                    _runtimeOptions.ShutdownCancellationGrace);
                ObserveLateWorkerFault(workers);
            }
        }
        finally
        {
            DrainPendingQueue();
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var userId in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _delivery.DeliverAsync(userId, cancellationToken);
                    BeanBotLog.WelcomeMessageSent(_logger, userId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    BeanBotLog.WelcomeDeliveryCanceled(_logger, userId);
                }
                catch (Exception exception)
                {
                    BeanBotLog.WelcomeMessageFailed(_logger, userId, exception);
                }
                finally
                {
                    ReleaseOutstanding(userId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void DrainPendingQueue()
    {
        while (_queue.Reader.TryRead(out var userId))
        {
            ReleaseOutstanding(userId);
        }
    }

    private void ReleaseOutstanding(ulong userId)
    {
        if (_outstandingUsers.TryRemove(userId, out _))
        {
            _outstandingSlots.Release();
        }
    }

    private static void ObserveLateWorkerFault(Task workers)
    {
        if (workers.IsCompleted)
        {
            _ = workers.Exception;
            return;
        }

        _ = workers.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
