using BeanBot.Logging;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Lifecycle;

internal enum DiscordLifecycleOutcomeKind
{
    Completed,
    FailedAfterCompletion,
    NeverStarted,
    Unfinished
}

internal sealed record DiscordLifecycleOutcome(
    DiscordLifecycleOutcomeKind Kind,
    string Sequence,
    string? Operation = null,
    Exception? Exception = null)
{
    public bool IsCompleted => Kind == DiscordLifecycleOutcomeKind.Completed;
}

internal sealed record DiscordLifecycleStep(string Name, Func<Task> Begin);

/// <summary>
/// Serializes all Discord.Net lifecycle sequences and retains ownership when the
/// caller's bounded wait ends before Discord.Net's underlying operation does.
/// </summary>
internal sealed class DiscordLifecycleCoordinator
{
    private readonly object _syncRoot = new();
    private readonly ILogger<DiscordLifecycleCoordinator> _logger;
    private ActiveSequence? _active;

    public DiscordLifecycleCoordinator(ILogger<DiscordLifecycleCoordinator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal DiscordLifecycleCoordinator(
        ILogger<DiscordLifecycleCoordinator> logger,
        Action<Exception, string, string>? lateFailureObserver)
        : this(logger)
    {
        LateFailureObserver = lateFailureObserver;
    }

    private Action<Exception, string, string>? LateFailureObserver { get; }

    public bool HasActiveSequence
    {
        get
        {
            lock (_syncRoot)
            {
                return _active is not null;
            }
        }
    }

    public async Task<DiscordLifecycleOutcome> RunSequenceAsync(
        string sequenceName,
        IReadOnlyList<DiscordLifecycleStep> steps,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceName);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationTimeout, TimeSpan.Zero);

        if (cancellationToken.IsCancellationRequested)
        {
            return new(
                DiscordLifecycleOutcomeKind.NeverStarted,
                sequenceName,
                Exception: new OperationCanceledException(cancellationToken));
        }

        var sequence = new ActiveSequence(sequenceName);
        lock (_syncRoot)
        {
            if (_active is not null)
            {
                return new(
                    DiscordLifecycleOutcomeKind.NeverStarted,
                    sequenceName,
                    Exception: new InvalidOperationException(
                        $"Discord lifecycle sequence '{_active.Name}' still owns the client."));
            }

            _active = sequence;
        }

        try
        {
            foreach (var step in steps)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new(
                        DiscordLifecycleOutcomeKind.NeverStarted,
                        sequenceName,
                        step.Name,
                        new OperationCanceledException(cancellationToken));
                }

                Task operation;
                try
                {
                    operation = step.Begin();
                }
                catch (Exception exception)
                {
                    return new(
                        DiscordLifecycleOutcomeKind.NeverStarted,
                        sequenceName,
                        step.Name,
                        exception);
                }

                try
                {
                    await operation.WaitAsync(operationTimeout, cancellationToken);
                }
                catch (Exception exception)
                {
                    if (operation.IsCompleted)
                    {
                        if (operation.IsFaulted || operation.IsCanceled)
                        {
                            try
                            {
                                await operation;
                            }
                            catch (Exception operationException)
                            {
                                exception = operationException;
                            }
                        }

                        return new(
                            DiscordLifecycleOutcomeKind.FailedAfterCompletion,
                            sequenceName,
                            step.Name,
                            exception);
                    }

                    TrackUnfinished(sequence, operation, step.Name);
                    return new(
                        DiscordLifecycleOutcomeKind.Unfinished,
                        sequenceName,
                        step.Name,
                        exception);
                }
            }

            return new(DiscordLifecycleOutcomeKind.Completed, sequenceName);
        }
        finally
        {
            lock (_syncRoot)
            {
                sequence.CallerFinished = true;
                ReleaseIfFinished(sequence);
            }
        }
    }

    private void TrackUnfinished(ActiveSequence sequence, Task operation, string operationName)
    {
        lock (_syncRoot)
        {
            sequence.UnfinishedOperation = operation;
        }

        _ = operation.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted)
                {
                    var exception = completedTask.Exception!;
                    if (LateFailureObserver is not null)
                    {
                        LateFailureObserver(exception, sequence.Name, operationName);
                    }
                    else
                    {
                        BeanBotLog.DiscordLifecycleLateFailure(
                            _logger,
                            sequence.Name,
                            operationName,
                            exception);
                    }
                }

                lock (_syncRoot)
                {
                    if (ReferenceEquals(sequence.UnfinishedOperation, completedTask))
                    {
                        sequence.UnfinishedOperation = null;
                    }

                    ReleaseIfFinished(sequence);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseIfFinished(ActiveSequence sequence)
    {
        if (sequence.CallerFinished &&
            sequence.UnfinishedOperation is null &&
            ReferenceEquals(_active, sequence))
        {
            _active = null;
        }
    }

    private sealed class ActiveSequence(string name)
    {
        public string Name { get; } = name;
        public bool CallerFinished { get; set; }
        public Task? UnfinishedOperation { get; set; }
    }
}
