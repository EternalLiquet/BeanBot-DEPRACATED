using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace BeanBot.Tests.Integration;

[Trait("Category", "MongoIntegration")]
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
                RoleMenuSelectionMode.Exclusive);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(57)]
    public async Task GetPageAsync_PagesEveryMenuDeterministicallyBothWays(int menuCount)
    {
        const int pageSize = 25;
        var databaseName = $"BeanBotRoleMenuPaging_{Guid.NewGuid():N}";
        var client = new MongoClient(_fixture.ConnectionString);
        var database = client.GetDatabase(databaseName);
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var repository = CreateRepository(database);
            var sharedCreationTime = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            for (var index = 0; index < menuCount; index++)
            {
                var menu = CreateSettings("1", "Same title");
                // Every third menu shares one creation time so the ID tie-breaker matters.
                menu.CreatedAtUtc = index % 3 == 0
                    ? sharedCreationTime
                    : sharedCreationTime.AddMinutes(-index);
                await repository.UpsertAsync(menu, cancellation.Token);
            }

            await repository.UpsertAsync(CreateSettings("2", "Other guild"), cancellation.Token);
            var expected = (await database.GetCollection<RoleMenuSettings>("roleMenus")
                    .Find(menu => menu.GuildId == "1")
                    .ToListAsync(cancellation.Token))
                .OrderByDescending(menu => menu.CreatedAtUtc)
                .ThenByDescending(menu => menu.Id)
                .Select(menu => menu.Id)
                .ToList();

            var pages = new List<List<RoleMenuSettings>>();
            RoleMenuPageCursor? cursor = null;
            do
            {
                var fetched = await repository.GetPageAsync(
                    "1",
                    cursor,
                    pageSize + 1,
                    cancellation.Token);
                var page = fetched.Take(pageSize).ToList();
                pages.Add(page);
                cursor = fetched.Count > pageSize
                    ? new RoleMenuPageCursor(
                        page[^1].CreatedAtUtc,
                        page[^1].Id,
                        RoleMenuPageDirection.Older)
                    : null;
            }
            while (cursor is not null);

            Assert.Equal(expected, pages.SelectMany(page => page).Select(menu => menu.Id));
            Assert.Equal(Math.Max(1, (menuCount + pageSize - 1) / pageSize), pages.Count);
            for (var index = pages.Count - 1; index > 0; index--)
            {
                var newer = await repository.GetPageAsync(
                    "1",
                    new RoleMenuPageCursor(
                        pages[index][0].CreatedAtUtc,
                        pages[index][0].Id,
                        RoleMenuPageDirection.Newer),
                    pageSize,
                    cancellation.Token);
                Assert.Equal(
                    pages[index - 1].Select(menu => menu.Id),
                    newer.Select(menu => menu.Id));
            }
        }
        finally
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            await client.DropDatabaseAsync(databaseName, cancellation.Token);
        }
    }

    [Fact]
    public async Task GetByMessageAsync_MatchesOnlyTheExactGuildChannelAndMessage()
    {
        var databaseName = $"BeanBotRoleMenuMessage_{Guid.NewGuid():N}";
        var client = new MongoClient(_fixture.ConnectionString);
        var database = client.GetDatabase(databaseName);
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var repository = CreateRepository(database);
            var target = CreateSettings("1", "Games");
            await repository.UpsertAsync(target, cancellation.Token);
            await repository.UpsertAsync(CreateSettings("2", "Games"), cancellation.Token);

            var found = await repository.GetByMessageAsync("1", "20", "30", 5, cancellation.Token);
            var wrongChannel = await repository.GetByMessageAsync("1", "21", "30", 5, cancellation.Token);
            var wrongMessage = await repository.GetByMessageAsync("1", "20", "31", 5, cancellation.Token);

            Assert.Equal(target.Id, Assert.Single(found).Id);
            Assert.Empty(wrongChannel);
            Assert.Empty(wrongMessage);
        }
        finally
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            await client.DropDatabaseAsync(databaseName, cancellation.Token);
        }
    }

    [Fact]
    public async Task DeleteBinding_DoesNotDeleteRepairedPanelOrNewerRevision()
    {
        var databaseName = $"BeanBotRoleMenuDelete_{Guid.NewGuid():N}";
        var client = new MongoClient(_fixture.ConnectionString);
        var database = client.GetDatabase(databaseName);
        try
        {
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            var repository = CreateRepository(database);
            var original = CreateSettings("1", "Games");
            await repository.UpsertAsync(original, cancellation.Token);
            var stale = Assert.IsType<RoleMenuSettings>(await repository.GetAsync(
                original.Id, "1", cancellation.Token));
            Assert.False(await repository.DeleteBindingAsync(stale, "2", "20", "30", cancellation.Token));
            Assert.False(await repository.DeleteBindingAsync(stale, "1", "21", "30", cancellation.Token));

            var repaired = new RoleMenuSettings(original.Id, "1", "20", "31", "Games", "", ["40"],
                RoleMenuSelectionMode.Multiple);
            await repository.UpsertAsync(repaired, cancellation.Token);
            Assert.False(await repository.DeleteBindingAsync(stale, "1", "20", "30", cancellation.Token));
            var current = Assert.IsType<RoleMenuSettings>(await repository.GetAsync(
                original.Id, "1", cancellation.Token));
            Assert.Equal("31", current.MessageId);
            Assert.True(await repository.DeleteBindingAsync(current, "1", "20", "31", cancellation.Token));
            Assert.Null(await repository.GetAsync(original.Id, "1", cancellation.Token));
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
