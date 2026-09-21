using MongoDB.Driver;

namespace BeanBot.Persistence.Repositories;

internal enum InstanceLeaseAcquireResult
{
    Acquired,
    Conflict
}

internal sealed record InstanceLeaseSnapshot(string HolderId, DateTimeOffset ExpiresAtUtc);

internal interface IInstanceLeaseStore
{
    Task<InstanceLeaseAcquireResult> TryAcquireAsync(
        string botIdentity,
        string holderId,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);

    Task<InstanceLeaseSnapshot?> GetAsync(
        string botIdentity,
        CancellationToken cancellationToken);

    Task<bool> TryRenewAsync(
        string botIdentity,
        string holderId,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);

    Task<bool> TryReleaseAsync(
        string botIdentity,
        string holderId,
        CancellationToken cancellationToken);
}

internal sealed class MongoInstanceLeaseStore : IInstanceLeaseStore
{
    internal const string CollectionName = "instanceLeases";

    private readonly IMongoCollection<InstanceLeaseDocument> _leases;

    public MongoInstanceLeaseStore(IMongoDatabase database)
    {
        _leases = (database ?? throw new ArgumentNullException(nameof(database)))
            .GetCollection<InstanceLeaseDocument>(CollectionName);
    }

    public async Task<InstanceLeaseAcquireResult> TryAcquireAsync(
        string botIdentity,
        string holderId,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(botIdentity, holderId);
        cancellationToken.ThrowIfCancellationRequested();

        var filter = Builders<InstanceLeaseDocument>.Filter.And(
            Builders<InstanceLeaseDocument>.Filter.Eq(lease => lease.Id, botIdentity),
            Builders<InstanceLeaseDocument>.Filter.Or(
                Builders<InstanceLeaseDocument>.Filter.Lte(lease => lease.ExpiresAtUtc, nowUtc),
                Builders<InstanceLeaseDocument>.Filter.Eq(lease => lease.HolderId, holderId)));
        var update = Builders<InstanceLeaseDocument>.Update
            .SetOnInsert(lease => lease.Id, botIdentity)
            .Set(lease => lease.HolderId, holderId)
            .Set(lease => lease.ExpiresAtUtc, expiresAtUtc);

        try
        {
            var result = await _leases.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                cancellationToken);
            if (!result.IsAcknowledged || (result.MatchedCount == 0 && result.UpsertedId is null))
            {
                throw new InvalidOperationException(
                    "Instance lease acquisition did not return a trustworthy acknowledgement.");
            }

            return InstanceLeaseAcquireResult.Acquired;
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return InstanceLeaseAcquireResult.Conflict;
        }
    }

    public async Task<InstanceLeaseSnapshot?> GetAsync(
        string botIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botIdentity);
        cancellationToken.ThrowIfCancellationRequested();

        var lease = await _leases
            .Find(lease => lease.Id == botIdentity)
            .FirstOrDefaultAsync(cancellationToken);
        return lease is null
            ? null
            : new InstanceLeaseSnapshot(lease.HolderId, lease.ExpiresAtUtc);
    }

    public async Task<bool> TryRenewAsync(
        string botIdentity,
        string holderId,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(botIdentity, holderId);
        cancellationToken.ThrowIfCancellationRequested();

        var filter = Builders<InstanceLeaseDocument>.Filter.And(
            Builders<InstanceLeaseDocument>.Filter.Eq(lease => lease.Id, botIdentity),
            Builders<InstanceLeaseDocument>.Filter.Eq(lease => lease.HolderId, holderId),
            Builders<InstanceLeaseDocument>.Filter.Gt(lease => lease.ExpiresAtUtc, nowUtc));
        var update = Builders<InstanceLeaseDocument>.Update
            .Set(lease => lease.ExpiresAtUtc, expiresAtUtc);
        var result = await _leases.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
        return result.IsAcknowledged && result.MatchedCount == 1;
    }

    public async Task<bool> TryReleaseAsync(
        string botIdentity,
        string holderId,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(botIdentity, holderId);
        cancellationToken.ThrowIfCancellationRequested();

        var filter = Builders<InstanceLeaseDocument>.Filter.And(
            Builders<InstanceLeaseDocument>.Filter.Eq(lease => lease.Id, botIdentity),
            Builders<InstanceLeaseDocument>.Filter.Eq(lease => lease.HolderId, holderId));
        var result = await _leases.DeleteOneAsync(filter, cancellationToken);
        return result.IsAcknowledged && result.DeletedCount == 1;
    }

    private static void ValidateIdentity(string botIdentity, string holderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(holderId);
    }

    internal sealed class InstanceLeaseDocument
    {
        public string Id { get; set; } = string.Empty;
        public string HolderId { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}
