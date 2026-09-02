using System.Net;
using System.Net.Sockets;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace BeanBot.Tests.Integration;

public sealed class MongoRoleReactRepositoryIntegrationTests
    : IClassFixture<MongoDbIntegrationFixture>
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private readonly MongoDbIntegrationFixture _fixture;

    public MongoRoleReactRepositoryIntegrationTests(MongoDbIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task InsertAndRead_PersistsRoleSettingsAcrossRepositoryInstances()
    {
        var databaseName = CreateDatabaseName();
        var firstClient = new MongoClient(_fixture.ConnectionString);
        var firstRepository = CreateRepository(firstClient.GetDatabase(databaseName));
        var settings = new RoleSettings(
            [new("123", "456")],
            "789",
            "101112",
            "131415");

        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            await firstRepository.InsertNewRoleSettings(settings, cancellation.Token);

            var secondClient = new MongoClient(_fixture.ConnectionString);
            var secondRepository = CreateRepository(secondClient.GetDatabase(databaseName));
            var persisted = await secondRepository.GetRoleSetting(131415UL, cancellation.Token);

            Assert.NotNull(persisted);
            Assert.Equal("789", persisted.GuildId);
            Assert.Equal("101112", persisted.ChannelId);
            Assert.Equal("131415", persisted.MessageId);
            var pair = Assert.Single(persisted.RoleEmotePairs);
            Assert.Equal("123", pair.RoleId);
            Assert.Equal("456", pair.EmojiId);
            Assert.Equal(DateTimeKind.Utc, persisted.LastAccessedUtc.Kind);
            Assert.NotEqual(default, persisted.LastAccessedUtc);
        }
        finally
        {
            await DropDatabaseAsync(firstClient, databaseName);
        }
    }

    [Fact]
    public async Task GetRecentRoleSettings_ReturnsNewestEntriesWithinLimit()
    {
        var databaseName = CreateDatabaseName();
        var client = new MongoClient(_fixture.ConnectionString);
        var database = client.GetDatabase(databaseName);
        var repository = CreateRepository(database);
        var now = DateTime.UtcNow;
        var oldest = CreateRoleSettings("1", now.AddDays(-31));
        var older = CreateRoleSettings("2", now.AddMinutes(-2));
        var newer = CreateRoleSettings("3", now.AddMinutes(-1));
        var newest = CreateRoleSettings("4", now);

        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            await database.GetCollection<RoleSettings>("roleSettings").InsertManyAsync(
                [oldest, older, newer, newest],
                cancellationToken: cancellation.Token);

            var recent = await repository.GetRecentRoleSettings(2, cancellation.Token);

            Assert.Collection(
                recent,
                setting => Assert.Equal("4", setting.MessageId),
                setting => Assert.Equal("3", setting.MessageId));
        }
        finally
        {
            await DropDatabaseAsync(client, databaseName);
        }
    }

    [Fact]
    public async Task GetRoleSetting_ReturnsNullWhenMongoCollectionHasNoMatch()
    {
        var databaseName = CreateDatabaseName();
        var client = new MongoClient(_fixture.ConnectionString);
        var repository = CreateRepository(client.GetDatabase(databaseName));

        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var persisted = await repository.GetRoleSetting(42UL, cancellation.Token);

            Assert.Null(persisted);
        }
        finally
        {
            await DropDatabaseAsync(client, databaseName);
        }
    }

    [Fact]
    public async Task GetRoleSetting_PropagatesBoundedMongoInfrastructureFailure()
    {
        using var nonMongoEndpoint = new TcpListener(IPAddress.Loopback, 0);
        nonMongoEndpoint.Start();
        var endpoint = (IPEndPoint)nonMongoEndpoint.LocalEndpoint;
        var settings = MongoClientSettings.FromConnectionString(
            $"mongodb://127.0.0.1:{endpoint.Port}");
        settings.ConnectTimeout = TimeSpan.FromMilliseconds(250);
        settings.SocketTimeout = TimeSpan.FromMilliseconds(250);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
        var repository = CreateRepository(
            new MongoClient(settings).GetDatabase(CreateDatabaseName()));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => repository.GetRoleSetting(42UL, cancellation.Token));

        Assert.Contains("selecting a server", exception.Message, StringComparison.Ordinal);
    }

    private static RoleReactRepository CreateRepository(IMongoDatabase database)
        => new(database, NullLogger<RoleReactRepository>.Instance);

    private static RoleSettings CreateRoleSettings(string messageId, DateTime lastAccessedUtc)
    {
        var settings = new RoleSettings([], "1", "2", messageId)
        {
            LastAccessedUtc = lastAccessedUtc
        };
        return settings;
    }

    private static string CreateDatabaseName()
        => $"BeanBotIntegration_{Guid.NewGuid():N}";

    private static async Task DropDatabaseAsync(MongoClient client, string databaseName)
    {
        using var cancellation = new CancellationTokenSource(OperationTimeout);
        await client.DropDatabaseAsync(databaseName, cancellation.Token);
    }
}

public sealed class MongoDbIntegrationFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);
    private const string MongoImage =
        "mongo:8.2.12-noble@sha256:dc23b0dde2221277b581dd76933f39f8a765fee9dbd99b9deb19184c063c061f";
    private readonly MongoDbContainer _container = new MongoDbBuilder(MongoImage).Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        using var cancellation = new CancellationTokenSource(StartupTimeout);
        await _container.StartAsync(cancellation.Token);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout);
    }
}
