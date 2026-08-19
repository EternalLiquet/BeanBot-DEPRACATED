using System.Globalization;
using BeanBot.Entities;
using BeanBot.Util;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace BeanBot.Repository;

internal interface IRoleSettingsStore
{
    Task InsertAsync(RoleSettings roleSettings, CancellationToken cancellationToken);
    Task<List<RoleSettings>> GetRecentAsync(DateTime oldestLastAccessedUtc, CancellationToken cancellationToken);
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

    public async Task<List<RoleSettings>> GetRecentAsync(
        DateTime oldestLastAccessedUtc,
        CancellationToken cancellationToken)
    {
        var filter = Builders<RoleSettings>.Filter.Where(
            result => result.LastAccessedUtc >= oldestLastAccessedUtc);
        using var results = await _roleSettings.FindAsync(
            filter,
            cancellationToken: cancellationToken);
        return await results.ToListAsync(cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _roleSettingsStore.GetRecentAsync(
            DateTime.UtcNow.AddDays(-30),
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
