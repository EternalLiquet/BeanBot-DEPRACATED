using System.Globalization;
using MongoDB.Driver;

namespace BeanBot.Persistence.Repositories;

internal enum DailyPunClaimResult
{
    Acquired,
    AlreadyClaimed
}

internal interface IDailyPunClaimStore
{
    Task<DailyPunClaimResult> TryClaimAsync(
        DateOnly chicagoDate,
        CancellationToken cancellationToken);
}

internal sealed class MongoDailyPunClaimStore : IDailyPunClaimStore
{
    internal const string CollectionName = "dailyPunCheckpoint";
    internal const string CheckpointId = "daily-pun";

    private readonly IMongoCollection<DailyPunCheckpoint> _checkpoints;

    public MongoDailyPunClaimStore(IMongoDatabase database)
    {
        _checkpoints = (database ?? throw new ArgumentNullException(nameof(database)))
            .GetCollection<DailyPunCheckpoint>(CollectionName);
    }

    public async Task<DailyPunClaimResult> TryClaimAsync(
        DateOnly chicagoDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claimedDate = chicagoDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filter = Builders<DailyPunCheckpoint>.Filter.And(
            Builders<DailyPunCheckpoint>.Filter.Eq(checkpoint => checkpoint.Id, CheckpointId),
            Builders<DailyPunCheckpoint>.Filter.Ne(checkpoint => checkpoint.ClaimedDate, claimedDate));
        var update = Builders<DailyPunCheckpoint>.Update
            .SetOnInsert(checkpoint => checkpoint.Id, CheckpointId)
            .Set(checkpoint => checkpoint.ClaimedDate, claimedDate);

        try
        {
            await _checkpoints.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                cancellationToken);
            return DailyPunClaimResult.Acquired;
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return DailyPunClaimResult.AlreadyClaimed;
        }
    }

    internal sealed class DailyPunCheckpoint
    {
        public string Id { get; set; } = CheckpointId;
        public string ClaimedDate { get; set; } = string.Empty;
    }
}
