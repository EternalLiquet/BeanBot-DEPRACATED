using System.Globalization;
using BeanBot.Logging;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeanBot.Hosting;

internal interface IInstanceLeaseHealth
{
    bool IsHeld { get; }
}

internal interface IInstanceLeaseClock
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemInstanceLeaseClock : IInstanceLeaseClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}

internal sealed record InstanceLeaseOptions(
    TimeSpan LeaseDuration,
    TimeSpan RenewInterval,
    TimeSpan SafetyMargin,
    TimeSpan OperationTimeout,
    TimeSpan UncertainRetryDelay)
{
    public static InstanceLeaseOptions Default { get; } = new(
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(1));

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LeaseDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RenewInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SafetyMargin, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(OperationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(UncertainRetryDelay, TimeSpan.Zero);
        if (RenewInterval + SafetyMargin >= LeaseDuration)
        {
            throw new ArgumentException(
                "The instance lease renewal interval and safety margin must fit inside the lease duration.");
        }
    }
}

internal sealed class BeanBotInstanceLease : IInstanceLeaseHealth, IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly IInstanceLeaseStore _store;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly IInstanceLeaseClock _clock;
    private readonly InstanceLeaseOptions _options;
    private readonly ILogger<BeanBotInstanceLease> _logger;
    private readonly string _holderId;
    private CancellationTokenSource? _renewalCancellation;
    private Task? _renewalTask;
    private string? _botIdentity;
    private DateTime _knownExpiresAtUtc;
    private bool _held;
    private bool _releaseSuppressed;
    private bool _disposed;

    public BeanBotInstanceLease(
        IInstanceLeaseStore store,
        IHostApplicationLifetime hostLifetime,
        IInstanceLeaseClock clock,
        InstanceLeaseOptions options,
        ILogger<BeanBotInstanceLease> logger,
        string? holderId = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hostLifetime = hostLifetime ?? throw new ArgumentNullException(nameof(hostLifetime));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _holderId = holderId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        ArgumentException.ThrowIfNullOrWhiteSpace(_holderId);
    }

    public bool IsHeld
    {
        get
        {
            lock (_syncRoot)
            {
                return _held && _clock.UtcNow < _knownExpiresAtUtc;
            }
        }
    }

    public async Task AcquireAsync(ulong botUserId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var botIdentity = botUserId.ToString(CultureInfo.InvariantCulture);

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            lock (_syncRoot)
            {
                if (_held)
                {
                    if (string.Equals(_botIdentity, botIdentity, StringComparison.Ordinal))
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        "This BeanBot process already owns an active-instance lease for another bot identity.");
                }
            }

            var nowUtc = _clock.UtcNow;
            var requestedExpiryUtc = nowUtc + _options.LeaseDuration;
            var confirmation = await TryAcquireAndReconcileAsync(
                botIdentity,
                nowUtc,
                requestedExpiryUtc,
                cancellationToken);
            if (confirmation.State == LeaseConfirmationState.NotHeld)
            {
                BeanBotLog.InstanceLeaseConflict(_logger, botIdentity);
                throw new InvalidOperationException(
                    "Another BeanBot process currently owns the active-instance lease for this Discord bot identity.");
            }

            if (confirmation.State != LeaseConfirmationState.Held)
            {
                BeanBotLog.InstanceLeaseOwnershipUnknown(_logger, botIdentity);
                throw new InvalidOperationException(
                    "BeanBot could not positively confirm active-instance lease ownership.");
            }

            var renewalCancellation = new CancellationTokenSource();
            lock (_syncRoot)
            {
                _botIdentity = botIdentity;
                _knownExpiresAtUtc = confirmation.ExpiresAtUtc;
                _held = true;
                _releaseSuppressed = false;
                _renewalCancellation = renewalCancellation;
                _renewalTask = RenewLoopAsync(botIdentity, renewalCancellation.Token);
            }

            BeanBotLog.InstanceLeaseAcquired(_logger, botIdentity);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void SuppressRelease()
    {
        CancellationTokenSource? renewalCancellation;
        lock (_syncRoot)
        {
            _releaseSuppressed = true;
            _held = false;
            renewalCancellation = _renewalCancellation;
        }

        renewalCancellation?.Cancel();
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            string? botIdentity;
            CancellationTokenSource? renewalCancellation;
            Task? renewalTask;
            lock (_syncRoot)
            {
                botIdentity = _botIdentity;
                renewalCancellation = _renewalCancellation;
                renewalTask = _renewalTask;
                _held = false;
                _renewalCancellation = null;
                _renewalTask = null;
                _botIdentity = null;
                _knownExpiresAtUtc = default;
            }

            renewalCancellation?.Cancel();
            if (renewalTask is not null)
            {
                try
                {
                    await renewalTask.WaitAsync(_options.OperationTimeout, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    BeanBotLog.InstanceLeaseRenewalDrainTimedOut(_logger);
                    ObserveLateFault(renewalTask);
                }
                catch (TimeoutException)
                {
                    BeanBotLog.InstanceLeaseRenewalDrainTimedOut(_logger);
                    ObserveLateFault(renewalTask);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }

            renewalCancellation?.Dispose();
            if (botIdentity is null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_releaseSuppressed)
                {
                    return;
                }
            }

            try
            {
                var released = await RunBoundedStoreOperationAsync(
                    token => _store.TryReleaseAsync(botIdentity, _holderId, token),
                    cancellationToken);
                if (released)
                {
                    BeanBotLog.InstanceLeaseReleased(_logger, botIdentity);
                }
                else
                {
                    BeanBotLog.InstanceLeaseReleaseNotOwned(_logger, botIdentity);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                BeanBotLog.InstanceLeaseReleaseFailed(_logger, botIdentity);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ReleaseAsync(CancellationToken.None);
        _lifecycleGate.Dispose();
    }

    private async Task RenewLoopAsync(string botIdentity, CancellationToken cancellationToken)
    {
        var uncertain = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = GetNextRenewalDelay(uncertain);
                if (delay <= TimeSpan.Zero)
                {
                    LoseOwnership(botIdentity);
                    return;
                }

                await _clock.DelayAsync(delay, cancellationToken);

                var nowUtc = _clock.UtcNow;
                DateTime knownExpiryUtc;
                lock (_syncRoot)
                {
                    if (!_held || !string.Equals(_botIdentity, botIdentity, StringComparison.Ordinal))
                    {
                        return;
                    }

                    knownExpiryUtc = _knownExpiresAtUtc;
                }

                var safetyDeadlineUtc = knownExpiryUtc - _options.SafetyMargin;
                var remainingSafety = safetyDeadlineUtc - nowUtc;
                if (remainingSafety <= TimeSpan.Zero)
                {
                    LoseOwnership(botIdentity);
                    return;
                }

                using var safetyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                safetyCancellation.CancelAfter(remainingSafety);
                var requestedExpiryUtc = nowUtc + _options.LeaseDuration;
                var confirmation = await TryRenewAndReconcileAsync(
                    botIdentity,
                    nowUtc,
                    requestedExpiryUtc,
                    safetyCancellation.Token);
                if (confirmation.State == LeaseConfirmationState.Held)
                {
                    var expiryAdvanced = confirmation.ExpiresAtUtc > knownExpiryUtc;
                    lock (_syncRoot)
                    {
                        if (_held && string.Equals(_botIdentity, botIdentity, StringComparison.Ordinal))
                        {
                            _knownExpiresAtUtc = confirmation.ExpiresAtUtc;
                        }
                    }

                    if (expiryAdvanced)
                    {
                        uncertain = false;
                        continue;
                    }

                    BeanBotLog.InstanceLeaseRenewalUncertain(_logger, botIdentity);
                    uncertain = true;
                    if (_clock.UtcNow >= safetyDeadlineUtc)
                    {
                        LoseOwnership(botIdentity);
                        return;
                    }

                    continue;
                }

                if (confirmation.State == LeaseConfirmationState.NotHeld)
                {
                    LoseOwnership(botIdentity);
                    return;
                }

                BeanBotLog.InstanceLeaseRenewalUncertain(_logger, botIdentity);
                uncertain = true;
                if (_clock.UtcNow >= safetyDeadlineUtc)
                {
                    LoseOwnership(botIdentity);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            LoseOwnership(botIdentity);
        }
    }

    private TimeSpan GetNextRenewalDelay(bool uncertain)
    {
        lock (_syncRoot)
        {
            var remaining = (_knownExpiresAtUtc - _options.SafetyMargin) - _clock.UtcNow;
            var preferred = uncertain ? _options.UncertainRetryDelay : _options.RenewInterval;
            return remaining <= preferred ? remaining : preferred;
        }
    }

    private async Task<LeaseConfirmation> TryAcquireAndReconcileAsync(
        string botIdentity,
        DateTime nowUtc,
        DateTime requestedExpiryUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunBoundedStoreOperationAsync(
                token => _store.TryAcquireAsync(
                    botIdentity,
                    _holderId,
                    nowUtc,
                    requestedExpiryUtc,
                    token),
                cancellationToken);
            return result == InstanceLeaseAcquireResult.Acquired
                ? LeaseConfirmation.Held(requestedExpiryUtc)
                : LeaseConfirmation.NotHeld();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await ReconcileAsync(botIdentity, cancellationToken);
        }
    }

    private async Task<LeaseConfirmation> TryRenewAndReconcileAsync(
        string botIdentity,
        DateTime nowUtc,
        DateTime requestedExpiryUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var renewed = await RunBoundedStoreOperationAsync(
                token => _store.TryRenewAsync(
                    botIdentity,
                    _holderId,
                    nowUtc,
                    requestedExpiryUtc,
                    token),
                cancellationToken);
            return renewed
                ? LeaseConfirmation.Held(requestedExpiryUtc)
                : LeaseConfirmation.NotHeld();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await ReconcileAsync(botIdentity, cancellationToken);
        }
    }

    private async Task<LeaseConfirmation> ReconcileAsync(
        string botIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await RunBoundedStoreOperationAsync(
                token => _store.GetAsync(botIdentity, token),
                cancellationToken);
            if (snapshot is null ||
                !string.Equals(snapshot.HolderId, _holderId, StringComparison.Ordinal) ||
                snapshot.ExpiresAtUtc <= _clock.UtcNow)
            {
                return LeaseConfirmation.NotHeld();
            }

            return LeaseConfirmation.Held(snapshot.ExpiresAtUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return LeaseConfirmation.Unknown();
        }
    }

    private async Task<T> RunBoundedStoreOperationAsync<T>(
        Func<CancellationToken, Task<T>> beginOperation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<T>? operation = null;
        try
        {
            operation = beginOperation(operationCancellation.Token);
            return await operation.WaitAsync(_options.OperationTimeout, cancellationToken);
        }
        catch
        {
            operationCancellation.Cancel();
            if (operation is { IsCompleted: false })
            {
                ObserveLateFault(operation);
            }

            throw;
        }
    }

    private void LoseOwnership(string botIdentity)
    {
        lock (_syncRoot)
        {
            if (!_held || !string.Equals(_botIdentity, botIdentity, StringComparison.Ordinal))
            {
                return;
            }

            _held = false;
        }

        _hostLifetime.StopApplication();
        BeanBotLog.InstanceLeaseLost(_logger, botIdentity);
    }

    private static void ObserveLateFault(Task operation)
    {
        _ = operation.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private enum LeaseConfirmationState
    {
        Held,
        NotHeld,
        Unknown
    }

    private readonly record struct LeaseConfirmation(
        LeaseConfirmationState State,
        DateTime ExpiresAtUtc)
    {
        public static LeaseConfirmation Held(DateTime expiresAtUtc)
            => new(LeaseConfirmationState.Held, expiresAtUtc);

        public static LeaseConfirmation NotHeld()
            => new(LeaseConfirmationState.NotHeld, default);

        public static LeaseConfirmation Unknown()
            => new(LeaseConfirmationState.Unknown, default);
    }
}