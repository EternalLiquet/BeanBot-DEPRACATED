using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Xunit;

namespace BeanBot.Tests.Integration;

[Trait("Category", "MongoIntegration")]
public sealed class MongoReactionRoleRepositoryIntegrationTests
    : IClassFixture<MongoDbIntegrationFixture>
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private readonly MongoDbIntegrationFixture _fixture;

    public MongoReactionRoleRepositoryIntegrationTests(MongoDbIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task InsertAndRead_PersistsRoleSettingsAcrossRepositoryInstances()
    {
        var databaseName = CreateDatabaseName();
        var firstClient = new MongoClient(_fixture.ConnectionString);
        var firstRepository = CreateRepository(firstClient.GetDatabase(databaseName));
        var settings = new ReactionRoleSettings(
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
            await database.GetCollection<ReactionRoleSettings>("roleSettings").InsertManyAsync(
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
    public async Task DeleteBinding_RequiresCurrentGuildChannelMessageAndDocumentId()
    {
        var databaseName = CreateDatabaseName();
        var client = new MongoClient(_fixture.ConnectionString);
        var repository = CreateRepository(client.GetDatabase(databaseName));
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            await repository.InsertNewRoleSettings(new ReactionRoleSettings([], "1", "2", "42"), cancellation.Token);
            var saved = Assert.IsType<ReactionRoleSettings>(await repository.GetRoleSetting(42, cancellation.Token));
            Assert.False(await repository.DeleteBindingAsync(saved, "9", "2", "42", cancellation.Token));
            Assert.False(await repository.DeleteBindingAsync(saved, "1", "9", "42", cancellation.Token));

            var collection = client.GetDatabase(databaseName).GetCollection<ReactionRoleSettings>("roleSettings");
            await collection.ReplaceOneAsync(
                candidate => candidate.Id == saved.Id,
                new ReactionRoleSettings([], "1", "2", "99") { Id = saved.Id },
                cancellationToken: cancellation.Token);
            Assert.False(await repository.DeleteBindingAsync(saved, "1", "2", "42", cancellation.Token));
            Assert.Equal("99", (await collection.Find(candidate => candidate.Id == saved.Id)
                .FirstOrDefaultAsync(cancellation.Token))?.MessageId);
        }
        finally
        {
            await DropDatabaseAsync(client, databaseName);
        }
    }

    [Fact]
    public async Task GetGuildPage_TraversesBeyondTwentyFiveWithoutCrossGuildRecords()
    {
        var databaseName = CreateDatabaseName();
        var client = new MongoClient(_fixture.ConnectionString);
        var repository = CreateRepository(client.GetDatabase(databaseName));
        var collection = client.GetDatabase(databaseName)
            .GetCollection<ReactionRoleSettings>("roleSettings");
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var records = Enumerable.Range(1, 30)
                .Select(index => new ReactionRoleSettings([], "1", "2", index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    Id = MongoDB.Bson.ObjectId.GenerateNewId()
                }).ToList();
            await collection.InsertManyAsync(records, cancellationToken: cancellation.Token);
            await collection.InsertOneAsync(
                new ReactionRoleSettings([], "9", "2", "100")
                { Id = MongoDB.Bson.ObjectId.GenerateNewId() },
                cancellationToken: cancellation.Token);

            var first = await repository.GetGuildPageAsync(1, null, false, 26,
                cancellation.Token);
            var second = await repository.GetGuildPageAsync(1, first[24].Id, false, 26,
                cancellation.Token);
            var back = await repository.GetGuildPageAsync(1, second[0].Id, true, 26,
                cancellation.Token);

            Assert.Equal(26, first.Count);
            Assert.Equal(5, second.Count);
            Assert.Equal(first.Take(25).Select(item => item.Id),
                back.Select(item => item.Id));
            Assert.All(first.Concat(second), item => Assert.Equal("1", item.GuildId));
            Assert.Equal(30, first.Take(25).Concat(second).Select(item => item.Id).Distinct().Count());
        }
        finally
        {
            await DropDatabaseAsync(client, databaseName);
        }
    }

    [Fact]
    public async Task DeleteBinding_RejectsChangedMappingsOnSameDocument()
    {
        var databaseName = CreateDatabaseName();
        var client = new MongoClient(_fixture.ConnectionString);
        var repository = CreateRepository(client.GetDatabase(databaseName));
        var collection = client.GetDatabase(databaseName)
            .GetCollection<ReactionRoleSettings>("roleSettings");
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            await repository.InsertNewRoleSettings(new ReactionRoleSettings(
                [new("4", "5")], "1", "2", "42"), cancellation.Token);
            var saved = Assert.IsType<ReactionRoleSettings>(
                await repository.GetRoleSetting(42, cancellation.Token));
            await collection.ReplaceOneAsync(item => item.Id == saved.Id,
                new ReactionRoleSettings([new("6", "7")], "1", "2", "42")
                { Id = saved.Id }, cancellationToken: cancellation.Token);

            Assert.False(await repository.DeleteBindingAsync(saved, "1", "2", "42",
                cancellation.Token));
            Assert.NotNull(await repository.GetRoleSetting(42, cancellation.Token));
        }
        finally
        {
            await DropDatabaseAsync(client, databaseName);
        }
    }

    private static ReactionRoleRepository CreateRepository(IMongoDatabase database)
        => new(database, NullLogger<ReactionRoleRepository>.Instance);

    private static ReactionRoleSettings CreateRoleSettings(string messageId, DateTime lastAccessedUtc)
    {
        var settings = new ReactionRoleSettings([], "1", "2", messageId)
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
    private const ushort MongoPort = 27017;
    private const string MongoImage =
        "mongo:8.2.12-noble@sha256:dc23b0dde2221277b581dd76933f39f8a765fee9dbd99b9deb19184c063c061f";
    private readonly IContainer _container = new ContainerBuilder(MongoImage)
        .WithPortBinding(MongoPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Waiting for connections"))
        .Build();

    public string ConnectionString
        => $"mongodb://{_container.Hostname}:{_container.GetMappedPublicPort(MongoPort)}";

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
