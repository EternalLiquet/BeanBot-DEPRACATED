using BeanBot.Persistence.Repositories;
using MongoDB.Driver;
using Xunit;

namespace BeanBot.Tests.Integration;

public sealed class MongoInstanceLeaseStoreIntegrationTests
    : IClassFixture<MongoDbIntegrationFixture>
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime StartUtc = new(2026, 9, 21, 14, 0, 0, DateTimeKind.Utc);
    private readonly MongoDbIntegrationFixture _fixture;

    public MongoInstanceLeaseStoreIntegrationTests(MongoDbIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TryAcquireAsync_ConcurrentSameBotHasSingleWinner()
    {
        var scope = CreateScope();
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var firstStore = new MongoInstanceLeaseStore(scope.Database);
            var secondStore = new MongoInstanceLeaseStore(scope.Database);

            var results = await Task.WhenAll(
                firstStore.TryAcquireAsync(
                    "bot-1",
                    "holder-a",
                    StartUtc,
                    StartUtc.AddSeconds(45),
                    cancellation.Token),
                secondStore.TryAcquireAsync(
                    "bot-1",
                    "holder-b",
                    StartUtc,
                    StartUtc.AddSeconds(45),
                    cancellation.Token));

            Assert.Single(results, result => result == InstanceLeaseAcquireResult.Acquired);
            Assert.Single(results, result => result == InstanceLeaseAcquireResult.Conflict);
            var snapshot = await firstStore.GetAsync("bot-1", cancellation.Token);
            Assert.NotNull(snapshot);
            Assert.Contains(snapshot.HolderId, new[] { "holder-a", "holder-b" });
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryAcquireAsync_DifferentBotIdentitiesDoNotBlockEachOther()
    {
        var scope = CreateScope();
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var store = new MongoInstanceLeaseStore(scope.Database);

            var first = await store.TryAcquireAsync(
                "bot-1",
                "holder-a",
                StartUtc,
                StartUtc.AddSeconds(45),
                cancellation.Token);
            var second = await store.TryAcquireAsync(
                "bot-2",
                "holder-b",
                StartUtc,
                StartUtc.AddSeconds(45),
                cancellation.Token);

            Assert.Equal(InstanceLeaseAcquireResult.Acquired, first);
            Assert.Equal(InstanceLeaseAcquireResult.Acquired, second);
            Assert.Equal("holder-a", (await store.GetAsync("bot-1", cancellation.Token))?.HolderId);
            Assert.Equal("holder-b", (await store.GetAsync("bot-2", cancellation.Token))?.HolderId);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExpiredLease_CanBeTakenOverAndStaleHolderCannotRenewOrReleaseSuccessor()
    {
        var scope = CreateScope();
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var store = new MongoInstanceLeaseStore(scope.Database);
            var originalExpiry = StartUtc.AddSeconds(10);
            Assert.Equal(
                InstanceLeaseAcquireResult.Acquired,
                await store.TryAcquireAsync(
                    "bot-1",
                    "holder-a",
                    StartUtc,
                    originalExpiry,
                    cancellation.Token));

            var takeoverTime = originalExpiry.AddSeconds(1);
            var successorExpiry = takeoverTime.AddSeconds(45);
            Assert.Equal(
                InstanceLeaseAcquireResult.Acquired,
                await store.TryAcquireAsync(
                    "bot-1",
                    "holder-b",
                    takeoverTime,
                    successorExpiry,
                    cancellation.Token));

            Assert.False(await store.TryRenewAsync(
                "bot-1",
                "holder-a",
                takeoverTime,
                takeoverTime.AddSeconds(45),
                cancellation.Token));
            Assert.False(await store.TryReleaseAsync(
                "bot-1",
                "holder-a",
                cancellation.Token));
            var snapshot = await store.GetAsync("bot-1", cancellation.Token);
            Assert.NotNull(snapshot);
            Assert.Equal("holder-b", snapshot.HolderId);
            Assert.Equal(successorExpiry, snapshot.ExpiresAtUtc);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExpiredLease_ConcurrentTakeoverStillHasSingleWinner()
    {
        var scope = CreateScope();
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var initialStore = new MongoInstanceLeaseStore(scope.Database);
            var expiry = StartUtc.AddSeconds(10);
            Assert.Equal(
                InstanceLeaseAcquireResult.Acquired,
                await initialStore.TryAcquireAsync(
                    "bot-1",
                    "holder-old",
                    StartUtc,
                    expiry,
                    cancellation.Token));

            var takeoverTime = expiry.AddSeconds(1);
            var firstStore = new MongoInstanceLeaseStore(scope.Database);
            var secondStore = new MongoInstanceLeaseStore(scope.Database);
            var results = await Task.WhenAll(
                firstStore.TryAcquireAsync(
                    "bot-1",
                    "holder-a",
                    takeoverTime,
                    takeoverTime.AddSeconds(45),
                    cancellation.Token),
                secondStore.TryAcquireAsync(
                    "bot-1",
                    "holder-b",
                    takeoverTime,
                    takeoverTime.AddSeconds(45),
                    cancellation.Token));

            Assert.Single(results, result => result == InstanceLeaseAcquireResult.Acquired);
            Assert.Single(results, result => result == InstanceLeaseAcquireResult.Conflict);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task RenewAndRelease_RequireExactHolderAndUnexpiredLease()
    {
        var scope = CreateScope();
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var store = new MongoInstanceLeaseStore(scope.Database);
            var expiry = StartUtc.AddSeconds(45);
            Assert.Equal(
                InstanceLeaseAcquireResult.Acquired,
                await store.TryAcquireAsync(
                    "bot-1",
                    "holder-a",
                    StartUtc,
                    expiry,
                    cancellation.Token));

            var renewedExpiry = StartUtc.AddSeconds(60);
            Assert.True(await store.TryRenewAsync(
                "bot-1",
                "holder-a",
                StartUtc.AddSeconds(15),
                renewedExpiry,
                cancellation.Token));
            Assert.False(await store.TryRenewAsync(
                "bot-1",
                "holder-b",
                StartUtc.AddSeconds(16),
                StartUtc.AddSeconds(61),
                cancellation.Token));
            Assert.False(await store.TryReleaseAsync("bot-1", "holder-b", cancellation.Token));
            Assert.True(await store.TryReleaseAsync("bot-1", "holder-a", cancellation.Token));
            Assert.Null(await store.GetAsync("bot-1", cancellation.Token));
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    private TestDatabaseScope CreateScope()
    {
        var databaseName = $"BeanBotInstanceLeaseIntegration_{Guid.NewGuid():N}";
        var client = new MongoClient(_fixture.ConnectionString);
        return new TestDatabaseScope(client, client.GetDatabase(databaseName), databaseName);
    }

    private sealed class TestDatabaseScope : IAsyncDisposable
    {
        private readonly MongoClient _client;
        private readonly string _databaseName;

        public TestDatabaseScope(MongoClient client, IMongoDatabase database, string databaseName)
        {
            _client = client;
            Database = database;
            _databaseName = databaseName;
        }

        public IMongoDatabase Database { get; }

        public async ValueTask DisposeAsync()
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            await _client.DropDatabaseAsync(_databaseName, cancellation.Token);
        }
    }
}
