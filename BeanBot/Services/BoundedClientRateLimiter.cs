using System;
using System.Collections.Generic;

namespace BeanBot.Services
{
    internal sealed class BoundedClientRateLimiter
    {
        private const int CleanupFrequency = 64;
        private readonly object _sync = new object();
        private readonly Dictionary<string, ClientEntry> _clients = new Dictionary<string, ClientEntry>(StringComparer.Ordinal);
        private readonly PriorityQueue<ExpirationCandidate, DateTimeOffset> _expirations = new PriorityQueue<ExpirationCandidate, DateTimeOffset>();
        private readonly TimeSpan _minimumPollInterval;
        private readonly TimeSpan _retentionPeriod;
        private readonly int _capacity;
        private readonly Func<DateTimeOffset> _getUtcNow;
        private int _requestCount;

        public BoundedClientRateLimiter(
            TimeSpan minimumPollInterval,
            int capacity,
            Func<DateTimeOffset> getUtcNow = null)
        {
            if (minimumPollInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumPollInterval));
            }

            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _minimumPollInterval = minimumPollInterval;
            _retentionPeriod = minimumPollInterval.Ticks <= TimeSpan.MaxValue.Ticks / 2
                ? TimeSpan.FromTicks(minimumPollInterval.Ticks * 2)
                : TimeSpan.MaxValue;
            _capacity = capacity;
            _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        }

        public bool IsRateLimited(string clientIdentifier, out int retryAfterSeconds)
        {
            if (string.IsNullOrWhiteSpace(clientIdentifier))
            {
                throw new ArgumentException("A client identifier is required.", nameof(clientIdentifier));
            }

            var now = _getUtcNow();
            lock (_sync)
            {
                _requestCount++;
                if (_requestCount % CleanupFrequency == 0)
                {
                    RemoveExpiredEntries(now);
                }

                if (_clients.TryGetValue(clientIdentifier, out var existingEntry))
                {
                    var nextAllowedAt = existingEntry.LastAllowedAt.Add(_minimumPollInterval);
                    if (now < nextAllowedAt)
                    {
                        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((nextAllowedAt - now).TotalSeconds));
                        return true;
                    }

                    TrackAllowedRequest(clientIdentifier, existingEntry, now);
                    retryAfterSeconds = 0;
                    return false;
                }

                if (_clients.Count >= _capacity)
                {
                    RemoveExpiredEntries(now);
                    if (_clients.Count >= _capacity)
                    {
                        // Preserve active rate limits instead of evicting them early. New
                        // clients retry after one poll interval when the bounded store is full.
                        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(_minimumPollInterval.TotalSeconds));
                        return true;
                    }
                }

                var entry = new ClientEntry();
                _clients.Add(clientIdentifier, entry);
                TrackAllowedRequest(clientIdentifier, entry, now);
                retryAfterSeconds = 0;
                return false;
            }
        }

        internal int CleanupStaleEntries()
        {
            lock (_sync)
            {
                return RemoveExpiredEntries(_getUtcNow());
            }
        }

        internal int TrackedClientCount
        {
            get
            {
                lock (_sync)
                {
                    return _clients.Count;
                }
            }
        }

        private void TrackAllowedRequest(string clientIdentifier, ClientEntry entry, DateTimeOffset now)
        {
            entry.LastAllowedAt = now;
            entry.ExpiresAt = AddSafely(now, _retentionPeriod);
            entry.Version++;
            _expirations.Enqueue(
                new ExpirationCandidate(clientIdentifier, entry.Version),
                entry.ExpiresAt);
        }

        private int RemoveExpiredEntries(DateTimeOffset now)
        {
            var removed = 0;
            while (_expirations.TryPeek(out var candidate, out var expiresAt) && expiresAt <= now)
            {
                _expirations.Dequeue();
                if (_clients.TryGetValue(candidate.ClientIdentifier, out var entry) &&
                    entry.Version == candidate.Version &&
                    entry.ExpiresAt <= now &&
                    _clients.Remove(candidate.ClientIdentifier))
                {
                    removed++;
                }
            }

            return removed;
        }

        private static DateTimeOffset AddSafely(DateTimeOffset value, TimeSpan duration)
        {
            if (duration == TimeSpan.MaxValue || duration > DateTimeOffset.MaxValue - value)
            {
                return DateTimeOffset.MaxValue;
            }

            return value.Add(duration);
        }

        private sealed class ClientEntry
        {
            public DateTimeOffset LastAllowedAt { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
            public long Version { get; set; }
        }

        private readonly struct ExpirationCandidate
        {
            public ExpirationCandidate(string clientIdentifier, long version)
            {
                ClientIdentifier = clientIdentifier;
                Version = version;
            }

            public string ClientIdentifier { get; }
            public long Version { get; }
        }
    }
}
