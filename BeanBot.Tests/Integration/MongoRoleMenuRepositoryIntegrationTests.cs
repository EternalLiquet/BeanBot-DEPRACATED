using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace BeanBot.Tests.Integration;

public sealed class MongoRoleMenuRepositoryIntegrationTests
    : IClassFixture<MongoDbIntegrationFixture>
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private readonly MongoDbIntegrationFixture _fixture;

    public MongoRoleMenuRepositoryIntegrationTests(MongoDbIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task InsertReadListAndDelete_AreRestartSafeAndGuildScoped()
    {
        var databaseName = $"BeanBotRoleMenuIntegration_{Guid.NewGuid():N}";
        var client = new MongoClient(_fixture.ConnectionString);
        var database = client.GetDatabase(databaseName);
        var firstMenu = CreateSettings("1", "First");
        var otherGuildMenu = CreateSettings("2", "Other guild");

        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var writer = CreateRepository(database);
            await writer.UpsertAsync(firstMenu, cancellation.Token);
            await writer.UpsertAsync(otherGuildMenu, cancellation.Token);
            var initiallyPersisted = Assert.IsType<RoleMenuSettings>(await writer.GetAsync(
                firstMenu.Id,
                "1",
                cancellation.Token));
            var originalCreatedAt = initiallyPersisted.CreatedAtUtc;
            var retriedMenu = new RoleMenuSettings(
                firstMenu.Id,
                "1",
                "20",
                "31",
                "First",
                string.Empty,
                ["40", "50"],
                RoleMenuSelectionMode.Multiple)
            {
                CreatedAtUtc = originalCreatedAt
            };
            await writer.UpsertAsync(retriedMenu, cancellation.Token);

            var restartedReader = CreateRepository(
                new MongoClient(_fixture.ConnectionString).GetDatabase(databaseName));
            var persisted = await restartedReader.GetAsync(
                firstMenu.Id,
                "1",
                cancellation.Token);
            var crossGuild = await restartedReader.GetAsync(
                firstMenu.Id,
                "2",
                cancellation.Token);
            var guildMenus = await restartedReader.GetByGuildAsync(
                "1",
                25,
                cancellation.Token);

            Assert.NotNull(persisted);
            Assert.Equal(retriedMenu.RoleIds, persisted.RoleIds);
            Assert.Equal("31", persisted.MessageId);
            Assert.Equal(originalCreatedAt, persisted.CreatedAtUtc);
            Assert.True(persisted.UpdatedAtUtc >= persisted.CreatedAtUtc);
            Assert.Equal(retriedMenu.SelectionMode, persisted.SelectionMode);
            Assert.Null(crossGuild);
            Assert.Equal(firstMenu.Id, Assert.Single(guildMenus).Id);
            Assert.False(await restartedReader.DeleteAsync(
                firstMenu.Id,
                "2",
                cancellation.Token));
            Assert.NotNull(await restartedReader.GetAsync(
                firstMenu.Id,
                "1",
                cancellation.Token));
            var crossGuildReplacement = new RoleMenuSettings(
                firstMenu.Id,
                "2",
                "20",
                "31",
                "Cross-guild replacement",
                string.Empty,
                ["40"],
                RoleMenuSelectionMode.Single);
            await Assert.ThrowsAnyAsync<MongoWriteException>(() =>
                restartedReader.UpsertAsync(crossGuildReplacement, cancellation.Token));
            var stillPersisted = Assert.IsType<RoleMenuSettings>(
                await restartedReader.GetAsync(
                firstMenu.Id,
                "1",
                cancellation.Token));
            Assert.Equal("First", stillPersisted.Title);
            Assert.True(await restartedReader.DeleteAsync(
                firstMenu.Id,
                "1",
                cancellation.Token));
            Assert.Null(await restartedReader.GetAsync(
                firstMenu.Id,
                "1",
                cancellation.Token));
        }
        finally
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            await client.DropDatabaseAsync(databaseName, cancellation.Token);
        }
    }

    private static RoleMenuRepository CreateRepository(IMongoDatabase database)
        => new(database, NullLogger<RoleMenuRepository>.Instance);

    private static RoleMenuSettings CreateSettings(string guildId, string title)
        => new(
            ObjectId.GenerateNewId(),
            guildId,
            "20",
            "30",
            title,
            string.Empty,
            ["40", "50"],
            RoleMenuSelectionMode.Multiple);
}
