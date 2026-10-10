using System.Globalization;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace BeanBot.Persistence.Repositories;

internal interface IReactionRoleSettingsStore
{
    Task InsertAsync(ReactionRoleSettings roleSettings, CancellationToken cancellationToken);
    Task<List<ReactionRoleSettings>> GetRecentAsync(
        DateTime oldestLastAccessedUtc,
        int limit,
        CancellationToken cancellationToken);
    Task<ReactionRoleSettings?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken);
    Task<bool> DeleteBindingAsync(ReactionRoleSettings settings, CancellationToken cancellationToken);
}

internal sealed class MongoReactionRoleSettingsStore : IReactionRoleSettingsStore
{
    private readonly IMongoCollection<ReactionRoleSettings> _roleSettings;

    public MongoReactionRoleSettingsStore(IMongoDatabase database)
    {
        _roleSettings = (database ?? throw new ArgumentNullException(nameof(database)))
            .GetCollection<ReactionRoleSettings>("roleSettings");
    }

    public Task InsertAsync(ReactionRoleSettings roleSettings, CancellationToken cancellationToken)
        => _roleSettings.InsertOneAsync(roleSettings, cancellationToken: cancellationToken);

    public Task<List<ReactionRoleSettings>> GetRecentAsync(
        DateTime oldestLastAccessedUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, 0);
        var filter = Builders<ReactionRoleSettings>.Filter.Where(
            result => result.LastAccessedUtc >= oldestLastAccessedUtc);
        return _roleSettings.Find(filter)
            .SortByDescending(result => result.LastAccessedUtc)
            .Limit(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<ReactionRoleSettings?> GetByMessageIdAsync(
        string messageId,
        CancellationToken cancellationToken)
    {
        var filter = Builders<ReactionRoleSettings>.Filter.Where(
            document => document.MessageId == messageId);
        return await _roleSettings.Find(filter).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> DeleteBindingAsync(ReactionRoleSettings settings, CancellationToken cancellationToken)
    {
        var filters = Builders<ReactionRoleSettings>.Filter;
        var filter = filters.And(
            filters.Eq(document => document.Id, settings.Id),
            filters.Eq(document => document.GuildId, settings.GuildId),
            filters.Eq(document => document.ChannelId, settings.ChannelId),
            filters.Eq(document => document.MessageId, settings.MessageId));
        return (await _roleSettings.DeleteOneAsync(filter, cancellationToken)).DeletedCount == 1;
    }
}

public sealed class ReactionRoleRepository
{
    private readonly IReactionRoleSettingsStore _reactionRoleSettingsStore;
    private readonly ILogger<ReactionRoleRepository> _logger;

    public ReactionRoleRepository(
        IMongoDatabase database,
        ILogger<ReactionRoleRepository> logger)
        : this(new MongoReactionRoleSettingsStore(database), logger)
    {
    }

    internal ReactionRoleRepository(
        IReactionRoleSettingsStore reactionRoleSettingsStore,
        ILogger<ReactionRoleRepository> logger)
    {
        _reactionRoleSettingsStore = reactionRoleSettingsStore ?? throw new ArgumentNullException(nameof(reactionRoleSettingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InsertNewRoleSettings(
        ReactionRoleSettings roleSettings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        roleSettings.LastAccessedUtc = DateTime.UtcNow;
        await _reactionRoleSettingsStore.InsertAsync(roleSettings, cancellationToken);
        BeanBotLog.ReactionRoleSettingsCreated(_logger, roleSettings.MessageId);
    }

    public Task<List<ReactionRoleSettings>> GetRecentRoleSettings(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, 0);
        cancellationToken.ThrowIfCancellationRequested();
        return _reactionRoleSettingsStore.GetRecentAsync(
            DateTime.UtcNow.AddDays(-30),
            limit,
            cancellationToken);
    }

    public Task<ReactionRoleSettings?> GetRoleSetting(
        ulong messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _reactionRoleSettingsStore.GetByMessageIdAsync(
            messageId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public async Task<bool> DeleteBindingAsync(
        ReactionRoleSettings settings,
        string guildId,
        string channelId,
        string messageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.GuildId != guildId || settings.ChannelId != channelId
            || settings.MessageId != messageId)
        {
            return false;
        }

        return await _reactionRoleSettingsStore.DeleteBindingAsync(settings, cancellationToken);
    }
}
