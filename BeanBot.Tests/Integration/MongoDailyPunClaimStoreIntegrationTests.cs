using BeanBot.Persistence.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace BeanBot.Tests.Integration;

public sealed class MongoDailyPunClaimStoreIntegrationTests
    : IClassFixture<MongoDbIntegrationFixture>
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private readonly MongoDbIntegrationFixture _fixture;

    public MongoDailyPunClaimStoreIntegrationTests(MongoDbIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TryClaimAsync_ConcurrentInstancesYieldSingleWinner()
    {
        var databaseName = CreateDatabaseName();
        var client = new MongoClient(_fixture.ConnectionString);
        var database = client.GetDatabase(databaseName);
        var firstStore = new MongoDailyPunClaimStore(database);
        var secondStore = new MongoDailyPunClaimStore(database);
        var chicagoDate = new DateOnly(2026, 9, 7);

        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var results = await Task.WhenAll(
                firstStore.TryClaimAsync(chicagoDate, cancellation.Token),
                secondStore.TryClaimAsync(chicagoDate, cancellation.Token));

            Assert.Single(results, result => result == DailyPunClaimResult.Acquired);
            Assert.Single(results, result => result == DailyPunClaimResult.AlreadyClaimed);

            var collection = database.GetCollection<BsonDocument>(MongoDailyPunClaimStore.CollectionName);
            var count = await collection.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: cancellation.Token);
            Assert.Equal(1, count);
        }
        finally
        {
            await DropDatabaseAsync(client, databaseName);
        }
    }

    [Fact]
    public async Task TryClaimAsync_NewDateReusesSingleCheckpointDocument()
    {
        var databaseName = CreateDatabaseName();
        var client = new MongoClient(_fixture.ConnectionString);
        var database = client.GetDatabase(databaseName);
        var store = new MongoDailyPunClaimStore(database);

        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            Assert.Equal(
                DailyPunClaimResult.Acquired,
                await store.TryClaimAsync(new DateOnly(2026, 9, 6), cancellation.Token));
            Assert.Equal(
                DailyPunClaimResult.Acquired,
                await store.TryClaimAsync(new DateOnly(2026, 9, 7), cancellation.Token));
            Assert.Equal(
                DailyPunClaimResult.AlreadyClaimed,
                await store.TryClaimAsync(new DateOnly(2026, 9, 7), cancellation.Token));

            Assert.Equal(
                DailyPunClaimResult.AlreadyClaimed,
                await store.TryClaimAsync(new DateOnly(2026, 9, 6), cancellation.Token));
            Assert.Equal(
                DailyPunClaimResult.AlreadyClaimed,
                await store.TryClaimAsync(new DateOnly(2026, 9, 7), cancellation.Token));

            var collection = database.GetCollection<BsonDocument>(MongoDailyPunClaimStore.CollectionName);
            var documents = await collection
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(cancellation.Token);
            var checkpoint = Assert.Single(documents);
            Assert.Equal(MongoDailyPunClaimStore.CheckpointId, checkpoint["_id"].AsString);
            Assert.Equal("2026-09-07", checkpoint["ClaimedDate"].AsString);
        }
        finally
        {
            await DropDatabaseAsync(client, databaseName);
        }
    }

    [Fact]
    public async Task TryClaimAsync_UnacknowledgedWriteConcernFailsClosed()
    {
        var databaseName = CreateDatabaseName();
        var cleanupClient = new MongoClient(_fixture.ConnectionString);
        var settings = MongoClientSettings.FromConnectionString(_fixture.ConnectionString);
        settings.WriteConcern = WriteConcern.Unacknowledged;
        var client = new MongoClient(settings);
        var store = new MongoDailyPunClaimStore(client.GetDatabase(databaseName));

        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.TryClaimAsync(new DateOnly(2026, 9, 7), cancellation.Token));

            Assert.Contains("trustworthy acknowledgement", exception.Message);
        }
        finally
        {
            await DropDatabaseAsync(cleanupClient, databaseName);
        }
    }

    private static string CreateDatabaseName()
        => $"BeanBotPunIntegration_{Guid.NewGuid():N}";

    private static async Task DropDatabaseAsync(MongoClient client, string databaseName)
    {
        using var cancellation = new CancellationTokenSource(OperationTimeout);
        await client.DropDatabaseAsync(databaseName, cancellation.Token);
    }
}
