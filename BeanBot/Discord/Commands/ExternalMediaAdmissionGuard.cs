namespace BeanBot.Discord.Commands;

internal enum ExternalMediaAdmissionResult
{
    Accepted,
    InFlight,
    CoolingDown,
    CapacityReached
}

public sealed class ExternalMediaAdmissionGuard
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<AdmissionKey, AdmissionEntry> _entries = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cooldown;
    private readonly int _capacity;

    public ExternalMediaAdmissionGuard(ExternalMediaCommandOptions options)
        : this(options, TimeProvider.System)
    {
    }

    internal ExternalMediaAdmissionGuard(
        ExternalMediaCommandOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _cooldown = options.AdmissionCooldown;
        _capacity = options.AdmissionCapacity;
    }

    internal async Task<ExternalMediaAdmissionResult> RunAsync(
        ulong userId,
        string budgetKey,
        Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(budgetKey);
        ArgumentNullException.ThrowIfNull(operation);

        var result = TryAcquire(userId, budgetKey, out var lease);
        if (result != ExternalMediaAdmissionResult.Accepted)
        {
            return result;
        }

        using (lease)
        {
            await operation();
        }

        return ExternalMediaAdmissionResult.Accepted;
    }

    private ExternalMediaAdmissionResult TryAcquire(
        ulong userId,
        string budgetKey,
        out AdmissionLease? lease)
    {
        var key = new AdmissionKey(userId, budgetKey);

        lock (_syncRoot)
        {
            var now = _timeProvider.GetTimestamp();
            RemoveExpiredEntries(now);

            if (_entries.TryGetValue(key, out var existingEntry))
            {
                lease = null;
                return existingEntry.IsActive
                    ? ExternalMediaAdmissionResult.InFlight
                    : ExternalMediaAdmissionResult.CoolingDown;
            }

            if (_entries.Count >= _capacity)
            {
                lease = null;
                return ExternalMediaAdmissionResult.CapacityReached;
            }

            var entry = new AdmissionEntry
            {
                IsActive = true
            };
            _entries.Add(key, entry);
            lease = new AdmissionLease(this, key, entry);
            return ExternalMediaAdmissionResult.Accepted;
        }
    }

    private void Release(AdmissionKey key, AdmissionEntry entry)
    {
        lock (_syncRoot)
        {
            if (!_entries.TryGetValue(key, out var currentEntry) ||
                !ReferenceEquals(currentEntry, entry) ||
                !currentEntry.IsActive)
            {
                return;
            }

            currentEntry.IsActive = false;
            currentEntry.CooldownStartedTimestamp = _timeProvider.GetTimestamp();
        }
    }

    private void RemoveExpiredEntries(long now)
    {
        List<AdmissionKey>? expiredKeys = null;
        foreach (var (key, entry) in _entries)
        {
            if (entry.IsActive ||
                _timeProvider.GetElapsedTime(entry.CooldownStartedTimestamp, now) < _cooldown)
            {
                continue;
            }

            expiredKeys ??= [];
            expiredKeys.Add(key);
        }

        if (expiredKeys is null)
        {
            return;
        }

        foreach (var key in expiredKeys)
        {
            _entries.Remove(key);
        }
    }

    private readonly record struct AdmissionKey(ulong UserId, string BudgetKey);

    private sealed class AdmissionEntry
    {
        internal bool IsActive { get; set; }

        internal long CooldownStartedTimestamp { get; set; }
    }

    private sealed class AdmissionLease(
        ExternalMediaAdmissionGuard owner,
        AdmissionKey key,
        AdmissionEntry entry) : IDisposable
    {
        private ExternalMediaAdmissionGuard? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(key, entry);
        }
    }
}
