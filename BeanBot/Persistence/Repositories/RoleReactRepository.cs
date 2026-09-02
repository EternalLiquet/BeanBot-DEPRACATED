using System.Globalization;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace BeanBot.Persistence.Repositories;

internal interface IRoleSettingsStore
{
    Task InsertAsync(RoleSettings roleSettings, CancellationToken cancellationToken);
    Task<List<RoleSettings>> GetRecentAsync(
        DateTime oldestLastAccessedUtc,
        int limit,
        CancellationToken cancellationToken);
    Task<RoleSettings?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken);
}

internal sealed class MongoRoleSettingsStore : IRoleSettingsStore
{
    private readonly IMongoCollection<RoleSettings> _roleSettings;

    public MongoRoleSettingsStore(IMongoDatabase database)
    {
        _roleSettings = (database ?? throw new ArgumentNullException(nameof(database)))
            .GetCollection<RoleSettings>("roleSettings");
    }

    public Task InsertAsync(RoleSettings roleSettings, CancellationToken cancellationToken)
        => _roleSettings.InsertOneAsync(roleSettings, cancellationToken: cancellationToken);

    public Task<List<RoleSettings>> GetRecentAsync(
        DateTime oldestLastAccessedUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, 0);
        var filter = Builders<RoleSettings>.Filter.Where(
            result => result.LastAccessedUtc >= oldestLastAccessedUtc);
        return _roleSettings.Find(filter)
            .SortByDescending(result => result.LastAccessedUtc)
            .Limit(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<RoleSettings?> GetByMessageIdAsync(
        string messageId,
        CancellationToken cancellationToken)
    {
        var filter = Builders<RoleSettings>.Filter.Where(
            document => document.MessageId == messageId);
        return await _roleSettings.Find(filter).FirstOrDefaultAsync(cancellationToken);
    }
}

public sealed class RoleReactRepository
{
    private readonly IRoleSettingsStore _roleSettingsStore;
    private readonly ILogger<RoleReactRepository> _logger;

    public RoleReactRepository(
        IMongoDatabase database,
        ILogger<RoleReactRepository> logger)
        : this(new MongoRoleSettingsStore(database), logger)
    {
    }

    internal RoleReactRepository(
        IRoleSettingsStore roleSettingsStore,
        ILogger<RoleReactRepository> logger)
    {
        _roleSettingsStore = roleSettingsStore ?? throw new ArgumentNullException(nameof(roleSettingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InsertNewRoleSettings(
        RoleSettings roleSettings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        roleSettings.LastAccessedUtc = DateTime.UtcNow;
        await _roleSettingsStore.InsertAsync(roleSettings, cancellationToken);
        BeanBotLog.ReactionRoleSettingsCreated(_logger, roleSettings.MessageId);
    }

    public Task<List<RoleSettings>> GetRecentRoleSettings(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, 0);
        cancellationToken.ThrowIfCancellationRequested();
        return _roleSettingsStore.GetRecentAsync(
            DateTime.UtcNow.AddDays(-30),
            limit,
            cancellationToken);
    }

    public Task<RoleSettings?> GetRoleSetting(
        ulong messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _roleSettingsStore.GetByMessageIdAsync(
            messageId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
    }
}
