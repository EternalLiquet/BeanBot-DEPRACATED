using BeanBot.Persistence.Models;
using Discord;
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
    DateTimeOffset ExpiresAtUtc,
    ulong? LegacyReactionRoleMessageId = null,
    string? LegacySourceFingerprint = null);

internal sealed record RoleMenuMigrationSelection(
    Guid Id,
    ulong GuildId,
    ulong UserId,
    ulong? TargetChannelId,
    ulong? TargetChannelGuildId,
    ChannelType? TargetChannelType,
    string? Title,
    string? Description,
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
    private readonly Dictionary<Guid, RoleMenuMigrationSelection> _migrationSelections = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), Guid> _selectionByOwner = [];
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly TimeSpan _lifetime;
    private readonly RoleMenuEditDraftRegistry _editDraftRegistry;

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
        _editDraftRegistry = new RoleMenuEditDraftRegistry(timeProvider, capacity, lifetime);
    }

    internal bool CreateMigrationSelection(
        ulong guildId, ulong userId, ulong? targetChannelId,
        ulong? targetChannelGuildId, ChannelType? targetChannelType,
        string? title, string? description,
        out RoleMenuMigrationSelection? selection)
    {
        lock (_syncRoot)
        {
            PurgeExpiredUnsafe();
            var owner = (guildId, userId);
            if (_selectionByOwner.Remove(owner, out var oldId))
            {
                _migrationSelections.Remove(oldId);
            }
            if (_migrationSelections.Count >= _capacity)
            {
                selection = null;
                return false;
            }
            selection = new RoleMenuMigrationSelection(
                Guid.NewGuid(), guildId, userId,
                targetChannelId, targetChannelGuildId, targetChannelType,
                title, description, _timeProvider.GetUtcNow().Add(_lifetime));
            _migrationSelections.Add(selection.Id, selection);
            _selectionByOwner.Add(owner, selection.Id);
            return true;
        }
    }

    internal bool TryGetMigrationSelection(
        Guid id, ulong guildId, ulong userId,
        out RoleMenuMigrationSelection? selection)
    {
        lock (_syncRoot)
        {
            PurgeExpiredUnsafe();
            if (_migrationSelections.TryGetValue(id, out selection)
                && selection.GuildId == guildId && selection.UserId == userId)
            {
                return true;
            }
            selection = null;
            return false;
        }
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
        => CreateCore(
            guildId,
            userId,
            targetChannelId,
            title,
            description,
            roleIds,
            selectionMode,
            ObjectId.GenerateNewId(),
            legacyReactionRoleMessageId: null,
            legacySourceFingerprint: null,
            out draft);

    internal RoleMenuDraftCreateStatus CreateMigration(
        ulong guildId,
        ulong userId,
        ulong targetChannelId,
        string title,
        string description,
        IReadOnlyCollection<ulong> roleIds,
        ObjectId menuId,
        ulong legacyReactionRoleMessageId,
        string legacySourceFingerprint,
        out RoleMenuDraft? draft)
    {
        if (menuId == ObjectId.Empty)
        {
            throw new ArgumentException("A deterministic menu ID is required.", nameof(menuId));
        }

        ArgumentOutOfRangeException.ThrowIfZero(legacyReactionRoleMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacySourceFingerprint);
        return CreateCore(
            guildId,
            userId,
            targetChannelId,
            title,
            description,
            roleIds,
            RoleMenuSelectionMode.Multiple,
            menuId,
            legacyReactionRoleMessageId,
            legacySourceFingerprint,
            out draft);
    }

    private RoleMenuDraftCreateStatus CreateCore(
        ulong guildId,
        ulong userId,
        ulong targetChannelId,
        string title,
        string description,
        IReadOnlyCollection<ulong> roleIds,
        RoleMenuSelectionMode selectionMode,
        ObjectId menuId,
        ulong? legacyReactionRoleMessageId,
        string? legacySourceFingerprint,
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
                menuId,
                guildId,
                userId,
                targetChannelId,
                title,
                description,
                [.. roleIds],
                selectionMode,
                now.Add(_lifetime),
                legacyReactionRoleMessageId,
                legacySourceFingerprint);
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

    internal RoleMenuEditDraftCreateStatus CreateEdit(
        ObjectId menuId,
        ulong guildId,
        ulong userId,
        string title,
        string description,
        IReadOnlyCollection<ulong> roleIds,
        RoleMenuSelectionMode selectionMode,
        out RoleMenuEditDraft? draft,
        RoleMenuEditSnapshot? snapshot = null)
        => _editDraftRegistry.Create(
            menuId,
            guildId,
            userId,
            title,
            description,
            roleIds,
            selectionMode,
            out draft,
            snapshot);

    internal RoleMenuEditDraftAccessStatus TryGetEdit(
        Guid draftId,
        ulong guildId,
        ulong userId,
        out RoleMenuEditDraft? draft)
        => _editDraftRegistry.TryGet(draftId, guildId, userId, out draft);

    internal RoleMenuEditDraftAccessStatus TryBeginEdit(
        Guid draftId,
        ulong guildId,
        ulong userId,
        out RoleMenuEditDraft? draft)
        => _editDraftRegistry.TryBeginSubmit(draftId, guildId, userId, out draft);

    internal void ReleaseEdit(Guid draftId, ulong guildId, ulong userId)
        => _editDraftRegistry.Release(draftId, guildId, userId);

    internal void CompleteEdit(Guid draftId, ulong guildId, ulong userId)
        => _editDraftRegistry.Complete(draftId, guildId, userId);

    private void PurgeExpiredUnsafe()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var selection in _migrationSelections.Values
                     .Where(candidate => candidate.ExpiresAtUtc <= now).ToList())
        {
            _migrationSelections.Remove(selection.Id);
            var owner = (selection.GuildId, selection.UserId);
            if (_selectionByOwner.TryGetValue(owner, out var currentId)
                && currentId == selection.Id)
            {
                _selectionByOwner.Remove(owner);
            }
        }
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
