using Discord;
using Discord.Commands;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Commands;

internal sealed class LegacyCommandReplyRejectedException : InvalidOperationException
{
    public LegacyCommandReplyRejectedException(string message)
        : base(message)
    {
    }
}

internal sealed class LegacyCommandReplyTimeoutException : TimeoutException
{
    public LegacyCommandReplyTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed partial class LegacyCommandReplySender : IDisposable
{
    internal const int DefaultCapacity = 16;
    internal static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<long, OwnedReplyOperation> _ownedOperations = [];
    private readonly CancellationToken _applicationStopping;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ILogger<LegacyCommandReplySender> _logger;
    private readonly int _capacity;
    private readonly TimeSpan _sendTimeout;
    private readonly TimeSpan _drainTimeout;
    private long _nextOperationId;
    private bool _stopping;
    private Task? _stopTask;

    public LegacyCommandReplySender(
        IHostApplicationLifetime applicationLifetime,
        ILogger<LegacyCommandReplySender> logger)
        : this(
            logger,
            DefaultCapacity,
            DefaultSendTimeout,
            DefaultDrainTimeout,
            applicationLifetime?.ApplicationStopping
                ?? throw new ArgumentNullException(nameof(applicationLifetime)))
    {
    }

    internal LegacyCommandReplySender(
        ILogger<LegacyCommandReplySender> logger,
        int capacity,
        TimeSpan sendTimeout,
        TimeSpan drainTimeout,
        CancellationToken applicationStopping)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sendTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(drainTimeout, TimeSpan.Zero);

        _applicationStopping = applicationStopping;
        _logger = logger;
        _capacity = capacity;
        _sendTimeout = sendTimeout;
        _drainTimeout = drainTimeout;
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

    public Task<IUserMessage> SendMessageAsync(
        ICommandContext context,
        string text = "",
        bool isTts = false,
        Embed? embed = null,
        AllowedMentions? allowedMentions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var replyKind = embed is null ? "text" : "embed";
        return RunAsync(
            requestOptions => context.Channel.SendMessageAsync(
                text,
                isTTS: isTts,
                embed: embed,
                options: requestOptions,
                allowedMentions: allowedMentions),
            replyKind);
    }

    public Task<IUserMessage> SendFileAsync(
        ICommandContext context,
        string filePath,
        string text = "",
        AllowedMentions? allowedMentions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return RunAsync(
            requestOptions => context.Channel.SendFileAsync(
                filePath,
                text,
                options: requestOptions,
                allowedMentions: allowedMentions),
            "file");
    }

    internal async Task<T> RunAsync<T>(
        Func<RequestOptions, Task<T>> beginSend,
        string replyKind)
    {
        ArgumentNullException.ThrowIfNull(beginSend);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyKind);
        _applicationStopping.ThrowIfCancellationRequested();

        var operation = ReserveOperation(replyKind);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _applicationStopping,
            _shutdown.Token);
        operationCancellation.CancelAfter(_sendTimeout);

        Task<T> sendTask;
        try
        {
            sendTask = beginSend(new RequestOptions
            {
                CancelToken = operationCancellation.Token
            }) ?? throw new InvalidOperationException("The Discord reply operation returned no task.");
        }
        catch
        {
            CompleteOperation(operation, null);
            throw;
        }

        ObserveAndOwnCompletion(operation, sendTask);

        try
        {
            return await sendTask.WaitAsync(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (
            _applicationStopping.IsCancellationRequested || _shutdown.IsCancellationRequested)
        {
            Volatile.Write(ref operation.WaiterDetached, 1);
            throw;
        }
        catch (OperationCanceledException exception) when (operationCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref operation.WaiterDetached, 1);
            ReplyTimedOut(_logger, replyKind, _sendTimeout);
            throw new LegacyCommandReplyTimeoutException(
                $"Legacy command {replyKind} reply timed out after {_sendTimeout}.",
                exception);
        }
    }

    public Task StopAsync()
    {
        Task stopTask;
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            _stopping = true;
            var pending = _ownedOperations.Values
                .Select(operation => operation.Completion.Task)
                .ToArray();
            _stopTask = DrainAsync(pending);
            stopTask = _stopTask;
        }

        _shutdown.Cancel();
        return stopTask;
    }

    public void Dispose()
    {
        _shutdown.Dispose();
    }

    private OwnedReplyOperation ReserveOperation(string replyKind)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                ReplyRejectedDuringShutdown(_logger, replyKind);
                throw new LegacyCommandReplyRejectedException(
                    "BeanBot is shutting down and is no longer accepting command replies.");
            }

            if (_ownedOperations.Count >= _capacity)
            {
                ReplyCapacityReached(_logger, replyKind, _ownedOperations.Count, _capacity);
                throw new LegacyCommandReplyRejectedException(
                    $"Legacy command reply capacity {_capacity} is exhausted.");
            }

            var operation = new OwnedReplyOperation(++_nextOperationId, replyKind);
            _ownedOperations.Add(operation.Id, operation);
            return operation;
        }
    }

    private void ObserveAndOwnCompletion(OwnedReplyOperation operation, Task sendTask)
    {
        _ = sendTask.ContinueWith(
            completedTask => CompleteOperation(operation, completedTask),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteOperation(OwnedReplyOperation operation, Task? sendTask)
    {
        if (sendTask?.IsFaulted == true)
        {
            var exception = sendTask.Exception;
            if (Volatile.Read(ref operation.WaiterDetached) != 0 && exception is not null)
            {
                ReplyLateFailure(_logger, operation.ReplyKind, exception);
            }
        }

        var removed = false;
        lock (_gate)
        {
            removed = _ownedOperations.Remove(operation.Id);
        }

        if (removed)
        {
            operation.Completion.TrySetResult();
        }
    }

    private async Task DrainAsync(Task[] pendingOperations)
    {
        if (pendingOperations.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pendingOperations).WaitAsync(_drainTimeout);
        }
        catch (TimeoutException)
        {
            ReplyDrainTimedOut(_logger, OwnedOperationCount, _drainTimeout);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejecting legacy command {ReplyKind} reply because BeanBot is shutting down")]
    private static partial void ReplyRejectedDuringShutdown(ILogger logger, string replyKind);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejecting legacy command {ReplyKind} reply because {ActiveReplyOperations} operations occupy the hard limit {ReplyOperationCapacity}")]
    private static partial void ReplyCapacityReached(
        ILogger logger,
        string replyKind,
        int activeReplyOperations,
        int replyOperationCapacity);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Legacy command {ReplyKind} reply exceeded its bounded wait {ReplyTimeout}; delivery outcome may be ambiguous and will not be retried")]
    private static partial void ReplyTimedOut(
        ILogger logger,
        string replyKind,
        TimeSpan replyTimeout);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Legacy command {ReplyKind} reply failed after its caller stopped waiting")]
    private static partial void ReplyLateFailure(
        ILogger logger,
        string replyKind,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Timed out draining {ActiveReplyOperations} legacy command reply operation(s) after {DrainTimeout}; Discord client teardown must remain blocked while they are still owned")]
    private static partial void ReplyDrainTimedOut(
        ILogger logger,
        int activeReplyOperations,
        TimeSpan drainTimeout);

    private sealed class OwnedReplyOperation
    {
        public OwnedReplyOperation(long id, string replyKind)
        {
            Id = id;
            ReplyKind = replyKind;
        }

        public long Id { get; }
        public string ReplyKind { get; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WaiterDetached;
    }
}
