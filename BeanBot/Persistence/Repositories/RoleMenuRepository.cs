using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BeanBot.Persistence.Repositories;

internal interface IRoleMenuStore
{
    Task InsertAsync(RoleMenuSettings settings, CancellationToken cancellationToken);
    Task UpsertAsync(RoleMenuSettings settings, CancellationToken cancellationToken);
    Task<RoleMenuSettings?> GetByIdAsync(
        ObjectId id,
        string guildId,
        CancellationToken cancellationToken);
    Task<List<RoleMenuSettings>> GetByGuildAsync(
        string guildId,
        int maximumResults,
        CancellationToken cancellationToken);
    Task<bool> DeleteAsync(
        ObjectId id,
        string guildId,
        CancellationToken cancellationToken);
}

internal sealed class MongoRoleMenuStore : IRoleMenuStore
{
    private readonly IMongoCollection<RoleMenuSettings> _roleMenus;

    public MongoRoleMenuStore(IMongoDatabase database)
    {
        _roleMenus = (database ?? throw new ArgumentNullException(nameof(database)))
            .GetCollection<RoleMenuSettings>("roleMenus");
    }

    public Task InsertAsync(RoleMenuSettings settings, CancellationToken cancellationToken)
        => _roleMenus.InsertOneAsync(settings, cancellationToken: cancellationToken);

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

    public Task<RoleMenuSettings?> GetByIdAsync(
        ObjectId id,
        string guildId,
        CancellationToken cancellationToken)
    {
        var filter = Builders<RoleMenuSettings>.Filter.And(
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.Id, id),
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.GuildId, guildId));
        return _roleMenus.Find(filter).FirstOrDefaultAsync(cancellationToken);
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

    public async Task InsertAsync(
        RoleMenuSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        settings.CreatedAtUtc = now;
        settings.UpdatedAtUtc = now;
        await _store.InsertAsync(settings, cancellationToken);
        BeanBotLog.RoleMenuSettingsCreated(_logger, settings.Id.ToString());
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
        BeanBotLog.RoleMenuSettingsSaved(_logger, settings.Id.ToString());
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
            BeanBotLog.RoleMenuSettingsDeleted(_logger, id.ToString());
        }

        return deleted;
    }
}
