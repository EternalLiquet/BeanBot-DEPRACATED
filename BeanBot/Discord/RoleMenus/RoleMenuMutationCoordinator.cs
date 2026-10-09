using System.Diagnostics.CodeAnalysis;

namespace BeanBot.Discord.RoleMenus;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposal would race timed-out leases that may complete after shutdown drain.")]
internal sealed class RoleMenuMutationCoordinator
{
    private const int StripeCount = 64;

    [SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "The process-lifetime owner intentionally preserves timed-out leases.")]
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

    private readonly AsyncReaderWriterStripe[] _menuLifecycles =
    [
        .. Enumerable.Range(0, StripeCount).Select(_ => new AsyncReaderWriterStripe())
    ];
    private readonly SemaphoreSlim[] _members =
    [
        .. Enumerable.Range(0, StripeCount).Select(_ => new SemaphoreSlim(1, 1))
    ];

    internal async Task<T> RunMenuWriteAsync<T>(
        string menuKey,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(menuKey);
        ArgumentNullException.ThrowIfNull(operation);

        using var menuLease = await GetMenuLifecycle(menuKey)
            .AcquireWriteAsync(cancellationToken);
        return await operation(cancellationToken);
    }

    internal async Task<T> RunMemberAsync<T>(
        string menuKey,
        string memberKey,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(menuKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberKey);
        ArgumentNullException.ThrowIfNull(operation);

        using var menuLease = await GetMenuLifecycle(menuKey)
            .AcquireReadAsync(cancellationToken);
        var member = _members[GetStripeIndex(memberKey)];
        await member.WaitAsync(cancellationToken);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            member.Release();
        }
    }

    private AsyncReaderWriterStripe GetMenuLifecycle(string menuKey)
        => _menuLifecycles[GetStripeIndex(menuKey)];

    internal static int GetStripeIndex(string key)
        => (int)((uint)StringComparer.Ordinal.GetHashCode(key) % StripeCount);
}
