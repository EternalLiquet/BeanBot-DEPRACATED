using BeanBot.Entities;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace BeanBot.Repository;

public sealed class RoleReactRepository : IRoleReactRepository
{
    private readonly IMongoCollection<RoleSettings> _roleSettingsCollection;
    private readonly ILogger<RoleReactRepository> _logger;

    public RoleReactRepository(IMongoDatabase database, ILogger<RoleReactRepository> logger)
    {
        _roleSettingsCollection = database.GetCollection<RoleSettings>("roleSettings");
        _logger = logger;

        _roleSettingsCollection.Indexes.CreateMany(
        [
            new CreateIndexModel<RoleSettings>(Builders<RoleSettings>.IndexKeys.Ascending(setting => setting.MessageId), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<RoleSettings>(Builders<RoleSettings>.IndexKeys.Descending(setting => setting.LastAccessed)),
        ]);
    }

    public async Task InsertNewRoleSettingsAsync(RoleSettings roleSettings, CancellationToken cancellationToken = default)
    {
        roleSettings.LastAccessed = DateTime.UtcNow;
        await _roleSettingsCollection.InsertOneAsync(roleSettings, cancellationToken: cancellationToken);
        _logger.LogDebug("Persisted role settings for message {MessageId}", roleSettings.MessageId);
    }

    public async Task<RoleSettings?> GetRoleSettingAsync(ulong messageId, CancellationToken cancellationToken = default)
    {
        var filter = Builders<RoleSettings>.Filter.Eq(setting => setting.MessageId, messageId);
        var roleSettings = await _roleSettingsCollection.Find(filter).FirstOrDefaultAsync(cancellationToken);
        _logger.LogDebug("Role settings lookup for message {MessageId} {Result}", messageId, roleSettings is null ? "missed" : "hit");
        return roleSettings;
    }

    public async Task<IReadOnlyList<RoleSettings>> GetRecentRoleSettingsAsync(CancellationToken cancellationToken = default)
    {
        var filter = Builders<RoleSettings>.Filter.Gte(setting => setting.LastAccessed, DateTime.UtcNow.AddDays(-30));
        var roleSettings = await _roleSettingsCollection.Find(filter).ToListAsync(cancellationToken);
        _logger.LogDebug("Loaded {RoleSettingsCount} recent role setting documents", roleSettings.Count);
        return roleSettings;
    }
}
