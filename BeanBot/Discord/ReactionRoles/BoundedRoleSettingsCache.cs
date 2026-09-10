using BeanBot.Persistence.Models;

namespace BeanBot.Discord.ReactionRoles;

internal sealed class BoundedRoleSettingsCache
{
    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = [];
    private readonly LinkedList<string> _recency = [];

    public BoundedRoleSettingsCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(string messageId, out RoleSettings? settings)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(messageId, out var entry))
            {
                settings = null;
                return false;
            }

            Touch(entry);
            settings = entry.Settings;
            return true;
        }
    }

    public void Set(RoleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.MessageId))
        {
            return;
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(settings.MessageId, out var existing))
            {
                existing.Settings = settings;
                Touch(existing);
                return;
            }

            if (_entries.Count == _capacity)
            {
                var leastRecent = _recency.Last;
                if (leastRecent != null)
                {
                    _entries.Remove(leastRecent.Value);
                    _recency.RemoveLast();
                }
            }

            var node = _recency.AddFirst(settings.MessageId);
            _entries.Add(settings.MessageId, new CacheEntry(settings, node));
        }
    }

    public void Seed(RoleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.MessageId))
        {
            return;
        }

        lock (_sync)
        {
            if (_entries.ContainsKey(settings.MessageId) || _entries.Count == _capacity)
            {
                return;
            }

            var node = _recency.AddLast(settings.MessageId);
            _entries.Add(settings.MessageId, new CacheEntry(settings, node));
        }
    }

    private void Touch(CacheEntry entry)
    {
        _recency.Remove(entry.Node);
        _recency.AddFirst(entry.Node);
    }

    private sealed class CacheEntry(RoleSettings settings, LinkedListNode<string> node)
    {
        public RoleSettings Settings { get; set; } = settings;
        public LinkedListNode<string> Node { get; } = node;
    }
}
