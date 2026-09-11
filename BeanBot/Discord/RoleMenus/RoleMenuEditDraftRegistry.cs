using BeanBot.Persistence.Models;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuEditDraftCreateStatus
{
    Created,
    CapacityReached,
    AlreadySubmitting
}

internal enum RoleMenuEditDraftAccessStatus
{
    Acquired,
    NotFound,
    WrongOwner,
    AlreadySubmitting
}

internal sealed record RoleMenuEditDraft(
    Guid Id,
    ObjectId MenuId,
    ulong GuildId,
    ulong UserId,
    string Title,
    string Description,
    IReadOnlyList<ulong> RoleIds,
    RoleMenuSelectionMode SelectionMode,
    DateTimeOffset ExpiresAtUtc);

internal sealed class RoleMenuEditDraftRegistry
{
    private sealed class DraftEntry
    {
        public required RoleMenuEditDraft Draft { get; set; }
        public bool IsSubmitting { get; set; }
    }

    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, DraftEntry> _drafts = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), Guid> _draftByOwner = [];
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly TimeSpan _lifetime;

    public RoleMenuEditDraftRegistry()
        : this(
            TimeProvider.System,
            RoleMenuConstants.MaximumDrafts,
            RoleMenuConstants.DraftLifetime)
    {
    }

    internal RoleMenuEditDraftRegistry(
        TimeProvider timeProvider,
        int capacity,
        TimeSpan lifetime)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        _capacity = capacity;
        _lifetime = lifetime;
    }

    internal RoleMenuEditDraftCreateStatus Create(
        ObjectId menuId,
        ulong guildId,
        ulong userId,
        string title,
        string description,
        IReadOnlyCollection<ulong> roleIds,
        RoleMenuSelectionMode selectionMode,
        out RoleMenuEditDraft? draft)
    {
        if (menuId == ObjectId.Empty)
        {
            throw new ArgumentException("A role menu ID is required.", nameof(menuId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(roleIds);

        lock (_syncRoot)
        {
            PurgeExpiredUnsafe();
            var owner = (guildId, userId);
            if (_draftByOwner.TryGetValue(owner, out var existingId)
                && _drafts.TryGetValue(existingId, out var existingEntry)
                && existingEntry.IsSubmitting)
            {
                draft = null;
                return RoleMenuEditDraftCreateStatus.AlreadySubmitting;
            }

            if (_draftByOwner.Remove(owner, out existingId))
            {
                _drafts.Remove(existingId);
            }

            if (_drafts.Count >= _capacity)
            {
                draft = null;
                return RoleMenuEditDraftCreateStatus.CapacityReached;
            }

            var now = _timeProvider.GetUtcNow();
            draft = new RoleMenuEditDraft(
                Guid.NewGuid(),
                menuId,
                guildId,
                userId,
                title,
                description,
                [.. roleIds],
                selectionMode,
                now.Add(_lifetime));
            _drafts[draft.Id] = new DraftEntry { Draft = draft };
            _draftByOwner[owner] = draft.Id;
            return RoleMenuEditDraftCreateStatus.Created;
        }
    }

    internal RoleMenuEditDraftAccessStatus TryGet(
        Guid draftId,
        ulong guildId,
        ulong userId,
        out RoleMenuEditDraft? draft)
    {
        lock (_syncRoot)
        {
            PurgeExpiredUnsafe();
            if (!_drafts.TryGetValue(draftId, out var entry))
            {
                draft = null;
                return RoleMenuEditDraftAccessStatus.NotFound;
            }

            if (entry.Draft.GuildId != guildId || entry.Draft.UserId != userId)
            {
                draft = null;
                return RoleMenuEditDraftAccessStatus.WrongOwner;
            }

            if (entry.IsSubmitting)
            {
                draft = null;
                return RoleMenuEditDraftAccessStatus.AlreadySubmitting;
            }

            entry.Draft = entry.Draft with
            {
                ExpiresAtUtc = _timeProvider.GetUtcNow().Add(_lifetime)
            };
            draft = entry.Draft;
            return RoleMenuEditDraftAccessStatus.Acquired;
        }
    }

    internal RoleMenuEditDraftAccessStatus TryBeginSubmit(
        Guid draftId,
        ulong guildId,
        ulong userId,
        out RoleMenuEditDraft? draft)
    {
        lock (_syncRoot)
        {
            PurgeExpiredUnsafe();
            if (!_drafts.TryGetValue(draftId, out var entry))
            {
                draft = null;
                return RoleMenuEditDraftAccessStatus.NotFound;
            }

            if (entry.Draft.GuildId != guildId || entry.Draft.UserId != userId)
            {
                draft = null;
                return RoleMenuEditDraftAccessStatus.WrongOwner;
            }

            if (entry.IsSubmitting)
            {
                draft = null;
                return RoleMenuEditDraftAccessStatus.AlreadySubmitting;
            }

            entry.IsSubmitting = true;
            entry.Draft = entry.Draft with
            {
                ExpiresAtUtc = _timeProvider.GetUtcNow().Add(_lifetime)
            };
            draft = entry.Draft;
            return RoleMenuEditDraftAccessStatus.Acquired;
        }
    }

    internal void Release(Guid draftId, ulong guildId, ulong userId)
    {
        lock (_syncRoot)
        {
            if (_drafts.TryGetValue(draftId, out var entry)
                && entry.Draft.GuildId == guildId
                && entry.Draft.UserId == userId)
            {
                entry.IsSubmitting = false;
                entry.Draft = entry.Draft with
                {
                    ExpiresAtUtc = _timeProvider.GetUtcNow().Add(_lifetime)
                };
            }
        }
    }

    internal void Complete(Guid draftId, ulong guildId, ulong userId)
    {
        lock (_syncRoot)
        {
            if (_drafts.TryGetValue(draftId, out var entry)
                && entry.Draft.GuildId == guildId
                && entry.Draft.UserId == userId)
            {
                RemoveUnsafe(entry.Draft);
            }
        }
    }

    private void PurgeExpiredUnsafe()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _drafts.Values
                     .Where(candidate => !candidate.IsSubmitting
                                         && candidate.Draft.ExpiresAtUtc <= now)
                     .ToList())
        {
            RemoveUnsafe(entry.Draft);
        }
    }

    private void RemoveUnsafe(RoleMenuEditDraft draft)
    {
        _drafts.Remove(draft.Id);
        var owner = (draft.GuildId, draft.UserId);
        if (_draftByOwner.TryGetValue(owner, out var ownerDraftId)
            && ownerDraftId == draft.Id)
        {
            _draftByOwner.Remove(owner);
        }
    }
}
