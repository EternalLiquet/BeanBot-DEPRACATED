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
    Task<RoleMenuSettings?> GetByMessageAsync(
        string guildId,
        string channelId,
        string messageId,
        CancellationToken cancellationToken);
    Task<List<RoleMenuSettings>> GetPageAsync(
        string guildId,
        DateTime? beforeCreatedAtUtc,
        ObjectId? beforeId,
        int maximumResults,
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

internal sealed class MongoRoleMenuStore : IRoleMenuStore, IDisposable
{
    private readonly IMongoCollection<RoleMenuSettings> _roleMenus;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexesReady;

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

    public async Task<RoleMenuSettings?> GetByMessageAsync(
        string guildId,
        string channelId,
        string messageId,
        CancellationToken cancellationToken)
    {
        await EnsureLookupIndexesAsync(cancellationToken);
        var filter = Builders<RoleMenuSettings>.Filter.And(
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.GuildId, guildId),
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.ChannelId, channelId),
            Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.MessageId, messageId));
        var matches = await _roleMenus.Find(filter).Limit(2).ToListAsync(cancellationToken);
        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task<List<RoleMenuSettings>> GetPageAsync(
        string guildId,
        DateTime? beforeCreatedAtUtc,
        ObjectId? beforeId,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        await EnsureLookupIndexesAsync(cancellationToken);
        var filter = Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.GuildId, guildId);
        if (beforeCreatedAtUtc.HasValue && beforeId.HasValue)
        {
            filter &= Builders<RoleMenuSettings>.Filter.Or(
                Builders<RoleMenuSettings>.Filter.Lt(candidate => candidate.CreatedAtUtc, beforeCreatedAtUtc.Value),
                Builders<RoleMenuSettings>.Filter.And(
                    Builders<RoleMenuSettings>.Filter.Eq(candidate => candidate.CreatedAtUtc, beforeCreatedAtUtc.Value),
                    Builders<RoleMenuSettings>.Filter.Lt(candidate => candidate.Id, beforeId.Value)));
        }

        return await _roleMenus.Find(filter)
            .SortByDescending(candidate => candidate.CreatedAtUtc)
            .ThenByDescending(candidate => candidate.Id)
            .Limit(maximumResults)
            .ToListAsync(cancellationToken);
    }

    private async Task EnsureLookupIndexesAsync(CancellationToken cancellationToken)
    {
        if (_indexesReady)
        {
            return;
        }

        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            if (_indexesReady)
            {
                return;
            }

            await _roleMenus.Indexes.CreateOneAsync(
                new CreateIndexModel<RoleMenuSettings>(
                    Builders<RoleMenuSettings>.IndexKeys
                        .Ascending(candidate => candidate.GuildId)
                        .Ascending(candidate => candidate.ChannelId)
                        .Ascending(candidate => candidate.MessageId),
                    new CreateIndexOptions { Name = "roleMenuMessageIdentity" }),
                cancellationToken: cancellationToken);
            await _roleMenus.Indexes.CreateOneAsync(
                new CreateIndexModel<RoleMenuSettings>(
                    Builders<RoleMenuSettings>.IndexKeys
                        .Ascending(candidate => candidate.GuildId)
                        .Descending(candidate => candidate.CreatedAtUtc)
                        .Descending(candidate => candidate.Id),
                    new CreateIndexOptions { Name = "roleMenuGuildPage" }),
                cancellationToken: cancellationToken);
            _indexesReady = true;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public void Dispose() => _indexGate.Dispose();

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

internal sealed class RoleMenuRepository : IDisposable
{
    private readonly IRoleMenuStore _store;
    private readonly ILogger<RoleMenuRepository> _logger;

    public RoleMenuRepository(
        IMongoDatabase database,
        ILogger<RoleMenuRepository> logger)
        : this(new MongoRoleMenuStore(database), logger)
    {
    }

    public void Dispose()
    {
        if (_store is IDisposable disposable)
        {
            disposable.Dispose();
        }
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

    public Task<RoleMenuSettings?> GetByMessageAsync(
        string guildId,
        string channelId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        cancellationToken.ThrowIfCancellationRequested();
        return _store.GetByMessageAsync(guildId, channelId, messageId, cancellationToken);
    }

    public Task<List<RoleMenuSettings>> GetPageAsync(
        string guildId,
        DateTime? beforeCreatedAtUtc,
        ObjectId? beforeId,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        if (beforeCreatedAtUtc.HasValue != beforeId.HasValue)
        {
            throw new ArgumentException("A complete role-menu page cursor is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _store.GetPageAsync(guildId, beforeCreatedAtUtc, beforeId, maximumResults, cancellationToken);
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
            BeanBotLog.RoleMenuSettingsDeleted(_logger, id);
        }

        return deleted;
    }
}
