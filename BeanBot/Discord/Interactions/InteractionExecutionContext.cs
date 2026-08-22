namespace BeanBot.Discord.Interactions;

internal sealed class InteractionExecutionContext
{
    private sealed record ExecutionState(CancellationToken CancellationToken);

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

    internal CancellationToken CancellationToken
        => _current.Value?.CancellationToken ?? System.Threading.CancellationToken.None;

    internal IDisposable Enter(CancellationToken cancellationToken)
    {
        var previous = _current.Value;
        _current.Value = new ExecutionState(cancellationToken);
        return new Scope(this, previous);
    }
}
