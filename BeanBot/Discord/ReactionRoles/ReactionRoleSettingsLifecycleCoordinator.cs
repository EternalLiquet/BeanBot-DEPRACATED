using System.Diagnostics.CodeAnalysis;

namespace BeanBot.Discord.ReactionRoles;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Process-lifetime stripes intentionally remain alive for late operations during shutdown.")]
internal sealed class ReactionRoleSettingsLifecycleCoordinator
{
    private const int StripeCount = 64;

    [SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "Disposal would race late readers or writers that still own a lease.")]
    private sealed class AsyncReaderWriterStripe
    {
        private sealed class Lease(Action release) : IDisposable
        {
            private Action? _release = release;

            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }

        private readonly SemaphoreSlim _turnstile = new(1, 1);
        private readonly SemaphoreSlim _roomEmpty = new(1, 1);
        private readonly SemaphoreSlim _readerMutex = new(1, 1);
        private int _readerCount;

        internal async Task<IDisposable> AcquireReadAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken);
            _turnstile.Release();

            await _readerMutex.WaitAsync(cancellationToken);
            try
            {
                _readerCount++;
                if (_readerCount == 1)
                {
                    try
                    {
                        await _roomEmpty.WaitAsync(cancellationToken);
                    }
                    catch
                    {
                        _readerCount--;
                        throw;
                    }
                }
            }
            finally
            {
                _readerMutex.Release();
            }

            return new Lease(ReleaseRead);
        }

        internal async Task<IDisposable> AcquireWriteAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken);
            try
            {
                await _roomEmpty.WaitAsync(cancellationToken);
            }
            catch
            {
                _turnstile.Release();
                throw;
            }

            return new Lease(() =>
            {
                _roomEmpty.Release();
                _turnstile.Release();
            });
        }

        private void ReleaseRead()
        {
            _readerMutex.Wait();
            try
            {
                _readerCount--;
                if (_readerCount == 0)
                {
                    _roomEmpty.Release();
                }
            }
            finally
            {
                _readerMutex.Release();
            }
        }
    }

    private readonly AsyncReaderWriterStripe[] _settings =
    [
        .. Enumerable.Range(0, StripeCount).Select(_ => new AsyncReaderWriterStripe())
    ];

    internal async Task RunReadAsync(
        ulong messageId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(messageId);
        ArgumentNullException.ThrowIfNull(operation);

        using var lease = await GetStripe(messageId).AcquireReadAsync(cancellationToken);
        await operation(cancellationToken);
    }

    internal async Task<T> RunReadAsync<T>(
        ulong messageId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(messageId);
        ArgumentNullException.ThrowIfNull(operation);

        using var lease = await GetStripe(messageId).AcquireReadAsync(cancellationToken);
        return await operation(cancellationToken);
    }

    internal async Task<T> RunWriteAsync<T>(
        ulong messageId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(messageId);
        ArgumentNullException.ThrowIfNull(operation);

        using var lease = await GetStripe(messageId).AcquireWriteAsync(cancellationToken);
        return await operation(cancellationToken);
    }

    private AsyncReaderWriterStripe GetStripe(ulong messageId)
        => _settings[(int)(messageId % StripeCount)];
}
