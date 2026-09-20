using Discord;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.ReactionRoles;

internal sealed class LegacyReactionRoleSetupOperationRejectedException : InvalidOperationException
{
    public LegacyReactionRoleSetupOperationRejectedException(string message)
        : base(message)
    {
    }
}

internal sealed class LegacyReactionRoleSetupOperationTimeoutException : TimeoutException
{
    public LegacyReactionRoleSetupOperationTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Owns the Discord REST mutations used only while publishing a legacy reaction-role panel.
/// Timed-out Discord tasks remain owned until they actually settle so repeated setup attempts
/// cannot create an unbounded tail of abandoned work.
/// </summary>
public sealed partial class LegacyReactionRoleSetupDiscordOperations
{
    internal const int DefaultCapacity = 8;
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultInterReactionDelay = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Dictionary<long, OwnedOperation> _ownedOperations = [];
    private readonly CancellationToken _applicationStopping;
    private readonly ILogger<LegacyReactionRoleSetupDiscordOperations> _logger;
    private readonly int _capacity;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _interReactionDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<CancellationTokenSource, TimeSpan> _scheduleTimeout;
    private long _nextOperationId;

    public LegacyReactionRoleSetupDiscordOperations(
        IHostApplicationLifetime applicationLifetime,
        ILogger<LegacyReactionRoleSetupDiscordOperations> logger)
        : this(
            logger,
            DefaultCapacity,
            DefaultOperationTimeout,
            DefaultInterReactionDelay,
            applicationLifetime?.ApplicationStopping
                ?? throw new ArgumentNullException(nameof(applicationLifetime)),
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            static (cancellation, timeout) => cancellation.CancelAfter(timeout))
    {
    }

    internal LegacyReactionRoleSetupDiscordOperations(
        ILogger<LegacyReactionRoleSetupDiscordOperations> logger,
        int capacity,
        TimeSpan operationTimeout,
        TimeSpan interReactionDelay,
        CancellationToken applicationStopping,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<CancellationTokenSource, TimeSpan> scheduleTimeout)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationTimeout, TimeSpan.Zero);
        if (interReactionDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interReactionDelay));
        }

        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(scheduleTimeout);

        _logger = logger;
        _capacity = capacity;
        _operationTimeout = operationTimeout;
        _interReactionDelay = interReactionDelay;
        _applicationStopping = applicationStopping;
        _delayAsync = delayAsync;
        _scheduleTimeout = scheduleTimeout;
    }

    internal int OwnedOperationCount
    {
        get
        {
            lock (_gate)
            {
                return _ownedOperations.Count;
            }
        }
    }

    internal bool HasPendingOperations => OwnedOperationCount > 0;

    public Task AddReactionsAsync(
        IUserMessage message,
        IEnumerable<IEmote> emotes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(emotes);

        var controls = emotes.ToList();
        return AddReactionsAsync(
            controls,
            (emote, requestOptions) => message.AddReactionAsync(emote, requestOptions),
            cancellationToken);
    }

    public Task DeleteMessageAsync(
        IUserMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return RunOperationAsync(
            requestOptions => message.DeleteAsync(requestOptions),
            "compensation-delete",
            cancellationToken);
    }

    internal async Task AddReactionsAsync<TControl>(
        IReadOnlyList<TControl> controls,
        Func<TControl, RequestOptions, Task> addReaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(addReaction);

        for (var index = 0; index < controls.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunOperationAsync(
                requestOptions => addReaction(controls[index], requestOptions),
                "reaction-add",
                cancellationToken);

            if (index + 1 < controls.Count && _interReactionDelay > TimeSpan.Zero)
            {
                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _applicationStopping,
                    cancellationToken);
                delayCancellation.Token.ThrowIfCancellationRequested();
                var delayTask = _delayAsync(_interReactionDelay, delayCancellation.Token)
                    ?? throw new InvalidOperationException("The legacy reaction-role pacing delay returned no task.");
                await delayTask;
            }
        }
    }

    internal async Task RunOperationAsync(
        Func<RequestOptions, Task> beginOperation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beginOperation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _applicationStopping,
            cancellationToken);
        callerCancellation.Token.ThrowIfCancellationRequested();

        var operation = ReserveOperation(operationName);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation.Token);

        Task operationTask;
        try
        {
            _scheduleTimeout(operationCancellation, _operationTimeout);
            operationCancellation.Token.ThrowIfCancellationRequested();
            operationTask = beginOperation(new RequestOptions
            {
                CancelToken = operationCancellation.Token
            }) ?? throw new InvalidOperationException(
                $"The legacy reaction-role Discord {operationName} operation returned no task.");
        }
        catch
        {
            CompleteOperation(operation, null);
            throw;
        }

        ObserveAndOwnCompletion(operation, operationTask);

        try
        {
            await operationTask.WaitAsync(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref operation.WaiterDetached, 1);
            throw;
        }
        catch (OperationCanceledException exception) when (operationCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref operation.WaiterDetached, 1);
            SetupOperationTimedOut(_logger, operationName, _operationTimeout);
            throw new LegacyReactionRoleSetupOperationTimeoutException(
                $"Legacy reaction-role Discord {operationName} timed out after {_operationTimeout}.",
                exception);
        }
    }

    private OwnedOperation ReserveOperation(string operationName)
    {
        lock (_gate)
        {
            if (_ownedOperations.Count >= _capacity)
            {
                SetupOperationCapacityReached(
                    _logger,
                    operationName,
                    _ownedOperations.Count,
                    _capacity);
                throw new LegacyReactionRoleSetupOperationRejectedException(
                    $"Legacy reaction-role setup Discord operation capacity {_capacity} is exhausted.");
            }

            var operation = new OwnedOperation(++_nextOperationId, operationName);
            _ownedOperations.Add(operation.Id, operation);
            return operation;
        }
    }

    private void ObserveAndOwnCompletion(OwnedOperation operation, Task operationTask)
    {
        _ = operationTask.ContinueWith(
            completedTask => CompleteOperation(operation, completedTask),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteOperation(OwnedOperation operation, Task? operationTask)
    {
        if (operationTask?.IsFaulted == true)
        {
            var exception = operationTask.Exception;
            if (Volatile.Read(ref operation.WaiterDetached) != 0 && exception is not null)
            {
                SetupOperationLateFailure(_logger, operation.OperationName, exception);
            }
        }

        lock (_gate)
        {
            _ownedOperations.Remove(operation.Id);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejecting legacy reaction-role setup Discord {Operation} because {ActiveOperations} operations occupy the hard limit {OperationCapacity}")]
    private static partial void SetupOperationCapacityReached(
        ILogger logger,
        string operation,
        int activeOperations,
        int operationCapacity);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Legacy reaction-role setup Discord {Operation} exceeded its bounded wait {OperationTimeout}; outcome may be ambiguous and will not be retried")]
    private static partial void SetupOperationTimedOut(
        ILogger logger,
        string operation,
        TimeSpan operationTimeout);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Legacy reaction-role setup Discord {Operation} failed after its caller stopped waiting")]
    private static partial void SetupOperationLateFailure(
        ILogger logger,
        string operation,
        Exception exception);

    private sealed class OwnedOperation
    {
        public OwnedOperation(long id, string operationName)
        {
            Id = id;
            OperationName = operationName;
        }

        public long Id { get; }
        public string OperationName { get; }
        public int WaiterDetached;
    }
}
