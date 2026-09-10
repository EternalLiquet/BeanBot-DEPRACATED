using BeanBot.Persistence.Models;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuDraftCreateStatus
{
    Created,
    CapacityReached,
    AlreadyPublishing
}

internal enum RoleMenuDraftAccessStatus
{
    Acquired,
    NotFound,
    WrongOwner,
    AlreadyPublishing
}

internal sealed record RoleMenuDraft(
    Guid Id,
    ObjectId MenuId,
    ulong GuildId,
    ulong UserId,
    ulong TargetChannelId,
    string Title,
    string Description,
    IReadOnlyList<ulong> RoleIds,
    RoleMenuSelectionMode SelectionMode,
    DateTimeOffset ExpiresAtUtc);

internal sealed class RoleMenuDraftRegistry
{
    private sealed class DraftEntry
    {
        public required RoleMenuDraft Draft { get; set; }
        public bool IsPublishing { get; set; }
    }

    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, DraftEntry> _drafts = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), Guid> _draftByOwner = [];
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly TimeSpan _lifetime;

    public RoleMenuDraftRegistry()
        : this(
            TimeProvider.System,
            RoleMenuConstants.MaximumDrafts,
            RoleMenuConstants.DraftLifetime)
    {
    }

    internal RoleMenuDraftRegistry(
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

    internal RoleMenuDraftCreateStatus Create(
        ulong guildId,
        ulong userId,
        ulong targetChannelId,
        string title,
        string description,
        IReadOnlyCollection<ulong> roleIds,
        RoleMenuSelectionMode selectionMode,
        out RoleMenuDraft? draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(roleIds);

        lock (_syncRoot)
        {
            PurgeExpiredUnsafe();
            var owner = (guildId, userId);
            if (_draftByOwner.TryGetValue(owner, out var existingId)
                && _drafts.TryGetValue(existingId, out var existingEntry)
                && existingEntry.IsPublishing)
            {
                draft = null;
                return RoleMenuDraftCreateStatus.AlreadyPublishing;
            }

            if (_draftByOwner.Remove(owner, out existingId))
            {
                _drafts.Remove(existingId);
            }

            if (_drafts.Count >= _capacity)
            {
                draft = null;
                return RoleMenuDraftCreateStatus.CapacityReached;
            }

            var now = _timeProvider.GetUtcNow();
            draft = new RoleMenuDraft(
                Guid.NewGuid(),
                ObjectId.GenerateNewId(),
                guildId,
                userId,
                targetChannelId,
                title,
                description,
                [.. roleIds],
                selectionMode,
                now.Add(_lifetime));
            _drafts[draft.Id] = new DraftEntry { Draft = draft };
            _draftByOwner[owner] = draft.Id;
            return RoleMenuDraftCreateStatus.Created;
        }
    }

    internal RoleMenuDraftAccessStatus TryBeginPublish(
        Guid draftId,
        ulong guildId,
        ulong userId,
        out RoleMenuDraft? draft)
    {
        lock (_syncRoot)
        {
            PurgeExpiredUnsafe();
            if (!_drafts.TryGetValue(draftId, out var entry))
            {
                draft = null;
                return RoleMenuDraftAccessStatus.NotFound;
            }

            if (entry.Draft.GuildId != guildId || entry.Draft.UserId != userId)
            {
                draft = null;
                return RoleMenuDraftAccessStatus.WrongOwner;
            }

            if (entry.IsPublishing)
            {
                draft = null;
                return RoleMenuDraftAccessStatus.AlreadyPublishing;
            }

            entry.IsPublishing = true;
            entry.Draft = entry.Draft with
            {
                ExpiresAtUtc = _timeProvider.GetUtcNow().Add(_lifetime)
            };
            draft = entry.Draft;
            return RoleMenuDraftAccessStatus.Acquired;
        }
    }

    internal bool Cancel(Guid draftId, ulong guildId, ulong userId)
    {
        lock (_syncRoot)
        {
            PurgeExpiredUnsafe();
            if (!_drafts.TryGetValue(draftId, out var entry)
                || entry.Draft.GuildId != guildId
                || entry.Draft.UserId != userId
                || entry.IsPublishing)
            {
                return false;
            }

            RemoveUnsafe(entry.Draft);
            return true;
        }
    }

    internal void ReleasePublish(Guid draftId, ulong guildId, ulong userId)
    {
        lock (_syncRoot)
        {
            if (_drafts.TryGetValue(draftId, out var entry)
                && entry.Draft.GuildId == guildId
                && entry.Draft.UserId == userId)
            {
                entry.IsPublishing = false;
                entry.Draft = entry.Draft with
                {
                    ExpiresAtUtc = _timeProvider.GetUtcNow().Add(_lifetime)
                };
            }
        }
    }

    internal void CompletePublish(Guid draftId, ulong guildId, ulong userId)
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
                     .Where(candidate => !candidate.IsPublishing
                                         && candidate.Draft.ExpiresAtUtc <= now)
                     .ToList())
        {
            RemoveUnsafe(entry.Draft);
        }
    }

    private void RemoveUnsafe(RoleMenuDraft draft)
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
