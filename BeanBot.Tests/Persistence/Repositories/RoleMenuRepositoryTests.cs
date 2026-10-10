using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Persistence.Repositories;

public class RoleMenuRepositoryTests
{
    [Fact]
    public async Task GetAsync_ScopesLookupByMenuAndGuild()
    {
        var settings = CreateSettings();
        var store = new FakeStore
        {
            GetById = (id, guildId, _) =>
            {
                Assert.Equal(settings.Id, id);
                Assert.Equal("1", guildId);
                return Task.FromResult<RoleMenuSettings?>(settings);
            }
        };

        var result = await CreateRepository(store).GetAsync(settings.Id, "1");

        Assert.Same(settings, result);
    }

    [Fact]
    public async Task GetAsync_RejectsEmptyMenuIdBeforeStoreCall()
    {
        var invoked = false;
        var store = new FakeStore
        {
            GetById = (_, _, _) =>
            {
                invoked = true;
                return Task.FromResult<RoleMenuSettings?>(null);
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateRepository(store).GetAsync(ObjectId.Empty, "1"));

        Assert.False(invoked);
    }

    [Fact]
    public async Task UpsertAsync_PreservesExistingCreationTimeAndUpdatesUtcTimestamp()
    {
        var settings = CreateSettings();
        var created = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        settings.CreatedAtUtc = created;
        var store = new FakeStore
        {
            Upsert = (actual, _) =>
            {
                Assert.Same(settings, actual);
                return Task.CompletedTask;
            }
        };

        await CreateRepository(store).UpsertAsync(settings);

        Assert.Equal(created, settings.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, settings.UpdatedAtUtc.Kind);
        Assert.True(settings.UpdatedAtUtc > created);
    }

    [Fact]
    public async Task GetByGuildAsync_EnforcesPositiveBound()
    {
        var repository = CreateRepository(new FakeStore());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.GetByGuildAsync("1", 0));
    }

    [Fact]
    public async Task GetPageAsync_PassesGuildCursorAndBound()
    {
        var settings = CreateSettings();
        var cursor = new RoleMenuPageCursor(
            new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc),
            settings.Id,
            RoleMenuPageDirection.Newer);
        var store = new FakeStore
        {
            GetPage = (guildId, actualCursor, maximumResults, _) =>
            {
                Assert.Equal("1", guildId);
                Assert.Equal(cursor, actualCursor);
                Assert.Equal(26, maximumResults);
                return Task.FromResult(new List<RoleMenuSettings> { settings });
            }
        };

        var page = await CreateRepository(store).GetPageAsync("1", cursor, 26);

        Assert.Same(settings, Assert.Single(page));
    }

    [Fact]
    public async Task GetPageAsync_RejectsUnboundedOrMalformedRequestsBeforeStoreCall()
    {
        var invoked = false;
        var repository = CreateRepository(new FakeStore
        {
            GetPage = (_, _, _, _) =>
            {
                invoked = true;
                return Task.FromResult(new List<RoleMenuSettings>());
            }
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.GetPageAsync("1", null, 0));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => repository.GetPageAsync(" ", null, 25));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetPageAsync(
            "1",
            new RoleMenuPageCursor(DateTime.UnixEpoch, ObjectId.Empty, RoleMenuPageDirection.Older),
            25));
        Assert.False(invoked);
    }

    [Fact]
    public async Task GetByMessageAsync_ScopesLookupToGuildChannelAndMessage()
    {
        var settings = CreateSettings();
        var store = new FakeStore
        {
            GetByMessage = (guildId, channelId, messageId, maximumResults, _) =>
            {
                Assert.Equal(("1", "2", "3", 5), (guildId, channelId, messageId, maximumResults));
                return Task.FromResult(new List<RoleMenuSettings> { settings });
            }
        };

        var matches = await CreateRepository(store).GetByMessageAsync("1", "2", "3", 5);

        Assert.Same(settings, Assert.Single(matches));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => CreateRepository(store).GetByMessageAsync("1", "", "3", 5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateRepository(store).GetByMessageAsync("1", "2", "3", 0));
    }

    [Fact]
    public async Task DeleteAsync_ReturnsStoreOutcomeAndPassesScope()
    {
        var settings = CreateSettings();
        var store = new FakeStore
        {
            Delete = (id, guildId, _) =>
            {
                Assert.Equal(settings.Id, id);
                Assert.Equal("1", guildId);
                return Task.FromResult(true);
            }
        };

        var deleted = await CreateRepository(store).DeleteAsync(settings.Id, "1");

        Assert.True(deleted);
    }

    [Fact]
    public async Task DeleteAsync_RejectsEmptyMenuIdBeforeStoreCall()
    {
        var invoked = false;
        var store = new FakeStore
        {
            Delete = (_, _, _) =>
            {
                invoked = true;
                return Task.FromResult(false);
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateRepository(store).DeleteAsync(ObjectId.Empty, "1"));

        Assert.False(invoked);
    }

    private static RoleMenuRepository CreateRepository(IRoleMenuStore store)
        => new(store, NullLogger<RoleMenuRepository>.Instance);

    private static RoleMenuSettings CreateSettings()
        => new(
            ObjectId.GenerateNewId(),
            "1",
            "2",
            "3",
            "Games",
            string.Empty,
            ["4", "5"],
            RoleMenuSelectionMode.Multiple);

    private sealed class FakeStore : IRoleMenuStore
    {
        public Func<RoleMenuSettings, CancellationToken, Task> Upsert { get; init; }
            = (_, _) => Task.CompletedTask;
        public Func<ObjectId, string, CancellationToken, Task<RoleMenuSettings?>> GetById
        { get; init; } = (_, _, _) => Task.FromResult<RoleMenuSettings?>(null);
        public Func<string, int, CancellationToken, Task<List<RoleMenuSettings>>> GetByGuild
        { get; init; } = (_, _, _) => Task.FromResult(new List<RoleMenuSettings>());
        public Func<ObjectId, string, CancellationToken, Task<bool>> Delete { get; init; }
            = (_, _, _) => Task.FromResult(false);
        public Func<string, string, string, int, CancellationToken, Task<List<RoleMenuSettings>>>
            GetByMessage
        { get; init; } = (_, _, _, _, _) => Task.FromResult(new List<RoleMenuSettings>());
        public Func<string, RoleMenuPageCursor?, int, CancellationToken, Task<List<RoleMenuSettings>>>
            GetPage
        { get; init; } = (_, _, _, _) => Task.FromResult(new List<RoleMenuSettings>());

        public Task UpsertAsync(
            RoleMenuSettings settings,
            CancellationToken cancellationToken)
            => Upsert(settings, cancellationToken);

        public Task<RoleMenuSettings?> GetByIdAsync(
            ObjectId id,
            string guildId,
            CancellationToken cancellationToken)
            => GetById(id, guildId, cancellationToken);

        public Task<List<RoleMenuSettings>> GetByGuildAsync(
            string guildId,
            int maximumResults,
            CancellationToken cancellationToken)
            => GetByGuild(guildId, maximumResults, cancellationToken);

        public Task<List<RoleMenuSettings>> GetByMessageAsync(
            string guildId,
            string channelId,
            string messageId,
            int maximumResults,
            CancellationToken cancellationToken)
            => GetByMessage(guildId, channelId, messageId, maximumResults, cancellationToken);

        public Task<List<RoleMenuSettings>> GetPageAsync(
            string guildId,
            RoleMenuPageCursor? cursor,
            int maximumResults,
            CancellationToken cancellationToken)
            => GetPage(guildId, cursor, maximumResults, cancellationToken);

        public Task<bool> DeleteAsync(
            ObjectId id,
            string guildId,
            CancellationToken cancellationToken)
            => Delete(id, guildId, cancellationToken);

        public Task<bool> DeleteBindingAsync(RoleMenuSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
