namespace BeanBot.Discord.Interactions;

internal enum InteractionInitialResponseState
{
    None,
    Attempted,
    Confirmed,
    Absent
}

internal readonly record struct InteractionInitialResponseSnapshot(
    InteractionInitialResponseState State,
    bool SupportsOriginalResponse);

public sealed class InteractionExecutionContext
{
    private sealed class ExecutionState
    {
        private readonly object _sync = new();
        private InteractionInitialResponseSnapshot _initialResponse = new(
            InteractionInitialResponseState.None,
            SupportsOriginalResponse: false);

        internal ExecutionState(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
        }

        internal CancellationToken CancellationToken { get; }

        internal InteractionInitialResponseSnapshot InitialResponse
        {
            get
            {
                lock (_sync)
                {
                    return _initialResponse;
                }
            }
        }

        internal void BeginInitialResponse(bool supportsOriginalResponse)
        {
            lock (_sync)
            {
                if (_initialResponse.State != InteractionInitialResponseState.None)
                {
                    throw new InvalidOperationException(
                        "An initial response has already been attempted for this interaction.");
                }

                _initialResponse = new InteractionInitialResponseSnapshot(
                    InteractionInitialResponseState.Attempted,
                    supportsOriginalResponse);
            }
        }

        internal void ConfirmInitialResponse()
            => TransitionInitialResponse(InteractionInitialResponseState.Confirmed);

        internal void MarkInitialResponseAbsent()
            => TransitionInitialResponse(InteractionInitialResponseState.Absent);

        private void TransitionInitialResponse(InteractionInitialResponseState state)
        {
            lock (_sync)
            {
                if (_initialResponse.State != InteractionInitialResponseState.Attempted)
                {
                    throw new InvalidOperationException(
                        "The initial response is not awaiting confirmation.");
                }

                _initialResponse = _initialResponse with { State = state };
            }
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly InteractionExecutionContext _owner;
        private readonly ExecutionState? _previous;
        private int _disposed;

        public Scope(
            InteractionExecutionContext owner,
            ExecutionState? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner._current.Value = _previous;
            }
        }
    }

    private readonly AsyncLocal<ExecutionState?> _current = new();

    internal InteractionExecutionContext()
    {
    }

    internal CancellationToken CancellationToken
        => _current.Value?.CancellationToken ?? System.Threading.CancellationToken.None;

    internal InteractionInitialResponseSnapshot InitialResponse
        => _current.Value?.InitialResponse ?? new InteractionInitialResponseSnapshot(
            InteractionInitialResponseState.None,
            SupportsOriginalResponse: false);

    internal IDisposable Enter(CancellationToken cancellationToken)
    {
        var previous = _current.Value;
        _current.Value = new ExecutionState(cancellationToken);
        return new Scope(this, previous);
    }

    internal void BeginInitialResponse(bool supportsOriginalResponse)
        => GetExecutionState().BeginInitialResponse(supportsOriginalResponse);

    internal void ConfirmInitialResponse()
        => GetExecutionState().ConfirmInitialResponse();

    internal void MarkInitialResponseAbsent()
        => GetExecutionState().MarkInitialResponseAbsent();

    private ExecutionState GetExecutionState()
        => _current.Value
            ?? throw new InvalidOperationException(
                "An interaction execution scope is required to track a response.");
}
