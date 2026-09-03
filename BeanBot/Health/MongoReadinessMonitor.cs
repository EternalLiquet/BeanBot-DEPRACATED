using BeanBot.Logging;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BeanBot.Health;

public interface IMongoReadinessProbe
{
    Task CheckAsync(CancellationToken cancellationToken);
}

internal sealed class MongoReadinessProbe : IMongoReadinessProbe
{
    private readonly IMongoDatabase _database;

    public MongoReadinessProbe(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument("ping", 1),
            cancellationToken: cancellationToken);
    }
}

internal readonly record struct MongoReadinessSnapshot(
    bool IsReachable,
    DateTimeOffset LastCheckedAtUtc);

public sealed class MongoReadinessMonitor
{
    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromSeconds(10);

    private readonly object _syncRoot = new();
    private readonly IMongoReadinessProbe _probe;
    private readonly ILogger<MongoReadinessMonitor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _freshnessWindow;
    private ProbeOperation? _inFlight;
    private CachedSnapshot? _lastSnapshot;
    private bool? _lastLoggedReachability;

    public MongoReadinessMonitor(
        IMongoReadinessProbe probe,
        ILogger<MongoReadinessMonitor> logger)
        : this(
            probe,
            logger,
            TimeProvider.System,
            DefaultProbeTimeout,
            DefaultFreshnessWindow)
    {
    }

    internal MongoReadinessMonitor(
        IMongoReadinessProbe probe,
        ILogger<MongoReadinessMonitor> logger,
        TimeProvider timeProvider,
        TimeSpan probeTimeout,
        TimeSpan freshnessWindow)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probeTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(freshnessWindow, TimeSpan.Zero);

        _probe = probe;
        _logger = logger;
        _timeProvider = timeProvider;
        _probeTimeout = probeTimeout;
        _freshnessWindow = freshnessWindow;
    }

    internal bool HasInFlightProbe
    {
        get
        {
            lock (_syncRoot)
            {
                return _inFlight is not null;
            }
        }
    }

    internal async Task<MongoReadinessSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ProbeOperation operation;
        var nowTimestamp = _timeProvider.GetTimestamp();
        lock (_syncRoot)
        {
            if (_lastSnapshot is { } cached
                && _timeProvider.GetElapsedTime(cached.RecordedTimestamp, nowTimestamp) <= _freshnessWindow)
            {
                return cached.Snapshot;
            }

            operation = _inFlight ?? StartProbeLocked();
        }

        return await WaitForProbeAsync(operation, cancellationToken);
    }

    private ProbeOperation StartProbeLocked()
    {
        var timeoutCancellation = new CancellationTokenSource();
        timeoutCancellation.CancelAfter(_probeTimeout);
        var operation = new ProbeOperation(
            _timeProvider.GetTimestamp(),
            timeoutCancellation,
            ExecuteProbeAsync(timeoutCancellation.Token));
        _inFlight = operation;
        _ = ObserveProbeCompletionAsync(operation);
        return operation;
    }

    private async Task<ProbeExecutionResult> ExecuteProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _probe.CheckAsync(cancellationToken);
            return new ProbeExecutionResult(
                true,
                null,
                cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ProbeExecutionResult(false, "timeout", true);
        }
        catch (Exception exception)
        {
            return new ProbeExecutionResult(
                false,
                exception.GetType().Name,
                cancellationToken.IsCancellationRequested);
        }
    }

    private async Task<MongoReadinessSnapshot> WaitForProbeAsync(
        ProbeOperation operation,
        CancellationToken cancellationToken)
    {
        var elapsed = _timeProvider.GetElapsedTime(operation.StartedTimestamp);
        var remaining = _probeTimeout - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            operation.Cancel();
            return RecordTimeout(operation);
        }

        try
        {
            var result = await operation.Task.WaitAsync(remaining, cancellationToken);
            return RecordCompleted(operation, result);
        }
        catch (TimeoutException)
        {
            operation.Cancel();
            return RecordTimeout(operation);
        }
    }

    private async Task ObserveProbeCompletionAsync(ProbeOperation operation)
    {
        var result = await operation.Task;
        _ = RecordCompleted(operation, result);
    }

    private MongoReadinessSnapshot RecordCompleted(
        ProbeOperation operation,
        ProbeExecutionResult result)
    {
        var snapshot = CreateSnapshot(result);
        var failureKind = GetFailureKind(result);
        var transition = MongoReadinessTransition.None;
        var ownsCompletion = false;

        lock (_syncRoot)
        {
            if (ReferenceEquals(_inFlight, operation))
            {
                _lastSnapshot = new CachedSnapshot(snapshot, _timeProvider.GetTimestamp());
                _inFlight = null;
                transition = UpdateLoggedReachabilityLocked(snapshot.IsReachable);
                ownsCompletion = true;
            }
        }

        if (ownsCompletion)
        {
            operation.Dispose();
            LogTransition(transition, failureKind);
        }

        return snapshot;
    }

    private MongoReadinessSnapshot RecordTimeout(ProbeOperation operation)
    {
        var snapshot = new MongoReadinessSnapshot(false, _timeProvider.GetUtcNow());
        var transition = MongoReadinessTransition.None;

        lock (_syncRoot)
        {
            if (ReferenceEquals(_inFlight, operation))
            {
                _lastSnapshot = new CachedSnapshot(snapshot, _timeProvider.GetTimestamp());
                transition = UpdateLoggedReachabilityLocked(false);
            }
        }

        LogTransition(transition, "timeout");
        return snapshot;
    }

    private MongoReadinessSnapshot CreateSnapshot(ProbeExecutionResult result)
    {
        var isReachable = result.Succeeded && !result.CancellationRequestedAtCompletion;
        return new MongoReadinessSnapshot(isReachable, _timeProvider.GetUtcNow());
    }

    private static string GetFailureKind(ProbeExecutionResult result)
    {
        if (result.CancellationRequestedAtCompletion)
        {
            return "timeout";
        }

        return result.FailureKind ?? "none";
    }

    private MongoReadinessTransition UpdateLoggedReachabilityLocked(bool isReachable)
    {
        if (_lastLoggedReachability == isReachable)
        {
            return MongoReadinessTransition.None;
        }

        var previous = _lastLoggedReachability;
        _lastLoggedReachability = isReachable;
        if (!isReachable)
        {
            return MongoReadinessTransition.Unavailable;
        }

        return previous == false
            ? MongoReadinessTransition.Recovered
            : MongoReadinessTransition.None;
    }

    private void LogTransition(MongoReadinessTransition transition, string failureKind)
    {
        switch (transition)
        {
            case MongoReadinessTransition.Unavailable:
                BeanBotLog.MongoReadinessUnavailable(_logger, failureKind);
                break;
            case MongoReadinessTransition.Recovered:
                BeanBotLog.MongoReadinessRecovered(_logger);
                break;
            case MongoReadinessTransition.None:
            default:
                break;
        }
    }

    private sealed class ProbeOperation : IDisposable
    {
        private readonly CancellationTokenSource _timeoutCancellation;

        public ProbeOperation(
            long startedTimestamp,
            CancellationTokenSource timeoutCancellation,
            Task<ProbeExecutionResult> task)
        {
            StartedTimestamp = startedTimestamp;
            _timeoutCancellation = timeoutCancellation;
            Task = task;
        }

        public long StartedTimestamp { get; }
        public Task<ProbeExecutionResult> Task { get; }

        public void Cancel()
        {
            try
            {
                _timeoutCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Probe completion already won ownership and disposed the timeout source.
            }
        }

        public void Dispose() => _timeoutCancellation.Dispose();
    }

    private readonly record struct ProbeExecutionResult(
        bool Succeeded,
        string? FailureKind,
        bool CancellationRequestedAtCompletion);

    private readonly record struct CachedSnapshot(
        MongoReadinessSnapshot Snapshot,
        long RecordedTimestamp);

    private enum MongoReadinessTransition
    {
        None,
        Unavailable,
        Recovered
    }
}
