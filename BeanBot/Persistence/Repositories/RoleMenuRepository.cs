using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BeanBot.Persistence.Repositories;

internal interface IRoleMenuStore
{
    Task UpsertAsync(RoleMenuSettings settings, CancellationToken cancellationToken);
    Task<RoleMenuSettings?> GetByIdAsync(
        ObjectId id,
        string guildId,
        CancellationToken cancellationToken);
    Task<List<RoleMenuSettings>> GetByGuildAsync(
        string guildId,
        int maximumResults,
        CancellationToken cancellationToken);
    Task<List<RoleMenuSettings>> GetByMessageAsync(
        string guildId,
        string channelId,
        string messageId,
        int maximumResults,
        CancellationToken cancellationToken);
    Task<List<RoleMenuSettings>> GetPageAsync(
        string guildId,
        RoleMenuPageCursor? cursor,
        int maximumResults,
        CancellationToken cancellationToken);
    Task<bool> DeleteAsync(
        ObjectId id,
        string guildId,
        CancellationToken cancellationToken);
}

internal enum RoleMenuPageDirection
{
    Older,
    Newer
}

internal readonly record struct RoleMenuPageCursor(
    DateTime CreatedAtUtc,
    ObjectId MenuId,
    RoleMenuPageDirection Direction);

internal sealed class MongoRoleMenuStore : IRoleMenuStore
{
    private readonly IMongoCollection<RoleMenuSettings> _roleMenus;

    public MongoRoleMenuStore(IMongoDatabase database)
    {
        _roleMenus = (database ?? throw new ArgumentNullException(nameof(database)))
            .GetCollection<RoleMenuSettings>("roleMenus");
    }

    public Task UpsertAsync(RoleMenuSettings settings, CancellationToken cancellationToken)
    {
        var filter = Builders<RoleMenuSettings>.Filter.And(
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.Id, settings.Id),
            Builders<RoleMenuSettings>.Filter.Eq(
                candidate => candidate.GuildId,
                settings.GuildId));
        return _roleMenus.ReplaceOneAsync(
            filter,
            settings,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<RoleMenuSettings?> GetByIdAsync(
        ObjectId id,
        string guildId,
        CancellationToken cancellationToken)
    {
        var filter = Builders<RoleMenuSettings>.Filter.And(
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.Id, id),
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.GuildId, guildId));
        return await _roleMenus.Find(filter).FirstOrDefaultAsync(cancellationToken);
    }

    public Task<List<RoleMenuSettings>> GetByGuildAsync(
        string guildId,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var filter = Builders<RoleMenuSettings>.Filter.Eq(
            candidate => candidate.GuildId,
            guildId);
        return _roleMenus.Find(filter)
            .SortByDescending(candidate => candidate.CreatedAtUtc)
            .Limit(maximumResults)
            .ToListAsync(cancellationToken);
    }

    public Task<List<RoleMenuSettings>> GetByMessageAsync(
        string guildId,
        string channelId,
        string messageId,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var filter = Builders<RoleMenuSettings>.Filter.And(
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.GuildId, guildId),
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.ChannelId, channelId),
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.MessageId, messageId));
        return _roleMenus.Find(filter)
            .Limit(maximumResults)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<RoleMenuSettings>> GetPageAsync(
        string guildId,
        RoleMenuPageCursor? cursor,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var filters = Builders<RoleMenuSettings>.Filter;
        var filter = filters.Eq(candidate => candidate.GuildId, guildId);
        var newerFirst = cursor is not { Direction: RoleMenuPageDirection.Newer };
        if (cursor is { } boundary)
        {
            // Creation time can repeat, so the menu ID breaks ties and keeps paging deterministic.
            filter &= newerFirst
                ? filters.Or(
                    filters.Lt(candidate => candidate.CreatedAtUtc, boundary.CreatedAtUtc),
                    filters.And(
                        filters.Eq(candidate => candidate.CreatedAtUtc, boundary.CreatedAtUtc),
                        filters.Lt(candidate => candidate.Id, boundary.MenuId)))
                : filters.Or(
                    filters.Gt(candidate => candidate.CreatedAtUtc, boundary.CreatedAtUtc),
                    filters.And(
                        filters.Eq(candidate => candidate.CreatedAtUtc, boundary.CreatedAtUtc),
                        filters.Gt(candidate => candidate.Id, boundary.MenuId)));
        }

        var sort = newerFirst
            ? Builders<RoleMenuSettings>.Sort
                .Descending(candidate => candidate.CreatedAtUtc)
                .Descending(candidate => candidate.Id)
            : Builders<RoleMenuSettings>.Sort
                .Ascending(candidate => candidate.CreatedAtUtc)
                .Ascending(candidate => candidate.Id);
        var page = await _roleMenus.Find(filter)
            .Sort(sort)
            .Limit(maximumResults)
            .ToListAsync(cancellationToken);
        if (!newerFirst)
        {
            page.Reverse();
        }

        return page;
    }

    public async Task<bool> DeleteAsync(
        ObjectId id,
        string guildId,
        CancellationToken cancellationToken)
    {
        var filter = Builders<RoleMenuSettings>.Filter.And(
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.Id, id),
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.GuildId, guildId));
        var result = await _roleMenus.DeleteOneAsync(filter, cancellationToken);
        return result.DeletedCount == 1;
    }
}

internal sealed class RoleMenuRepository
{
    private readonly IRoleMenuStore _store;
    private readonly ILogger<RoleMenuRepository> _logger;

    public RoleMenuRepository(
        IMongoDatabase database,
        ILogger<RoleMenuRepository> logger)
        : this(new MongoRoleMenuStore(database), logger)
    {
    }

    internal RoleMenuRepository(
        IRoleMenuStore store,
        ILogger<RoleMenuRepository> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task UpsertAsync(
        RoleMenuSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        if (settings.CreatedAtUtc == default)
        {
            settings.CreatedAtUtc = now;
        }

        settings.UpdatedAtUtc = now;
        await _store.UpsertAsync(settings, cancellationToken);
        BeanBotLog.RoleMenuSettingsSaved(_logger, settings.Id);
    }

    public Task<RoleMenuSettings?> GetAsync(
        ObjectId id,
        string guildId,
        CancellationToken cancellationToken = default)
    {
        if (id == ObjectId.Empty)
        {
            throw new ArgumentException("A role menu ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        cancellationToken.ThrowIfCancellationRequested();
        return _store.GetByIdAsync(id, guildId, cancellationToken);
    }

    public Task<List<RoleMenuSettings>> GetByGuildAsync(
        string guildId,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        cancellationToken.ThrowIfCancellationRequested();
        return _store.GetByGuildAsync(guildId, maximumResults, cancellationToken);
    }

    public Task<List<RoleMenuSettings>> GetByMessageAsync(
        string guildId,
        string channelId,
        string messageId,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        cancellationToken.ThrowIfCancellationRequested();
        return _store.GetByMessageAsync(
            guildId,
            channelId,
            messageId,
            maximumResults,
            cancellationToken);
    }

    /// <summary>
    /// Reads one bounded page of a guild's menus, newest first. A cursor continues from a page
    /// edge: <see cref="RoleMenuPageDirection.Older"/> reads past it and
    /// <see cref="RoleMenuPageDirection.Newer"/> reads the menus just before it. Results within a
    /// page are always newest first.
    /// </summary>
    public Task<List<RoleMenuSettings>> GetPageAsync(
        string guildId,
        RoleMenuPageCursor? cursor,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        if (cursor is { MenuId: var menuId } && menuId == ObjectId.Empty)
        {
            throw new ArgumentException("A page cursor needs a role menu ID.", nameof(cursor));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _store.GetPageAsync(guildId, cursor, maximumResults, cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        ObjectId id,
        string guildId,
        CancellationToken cancellationToken = default)
    {
        if (id == ObjectId.Empty)
        {
            throw new ArgumentException("A role menu ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        cancellationToken.ThrowIfCancellationRequested();
        var deleted = await _store.DeleteAsync(id, guildId, cancellationToken);
        if (deleted)
        {
            BeanBotLog.RoleMenuSettingsDeleted(_logger, id);
        }

        return deleted;
    }
}
