using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Persistence.Repositories;

public class RoleMenuRepositoryTests
{
    [Fact]
    public async Task InsertAsync_StampsUtcDatesAndPassesCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var settings = CreateSettings();
        var before = DateTime.UtcNow;
        var store = new FakeStore
        {
            Insert = (actual, cancellationToken) =>
            {
                Assert.Same(settings, actual);
                Assert.Equal(cancellation.Token, cancellationToken);
                return Task.CompletedTask;
            }
        };

        await CreateRepository(store).InsertAsync(settings, cancellation.Token);

        Assert.Equal(DateTimeKind.Utc, settings.CreatedAtUtc.Kind);
        Assert.Equal(settings.CreatedAtUtc, settings.UpdatedAtUtc);
        Assert.InRange(settings.CreatedAtUtc, before, DateTime.UtcNow);
    }

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
        public Func<RoleMenuSettings, CancellationToken, Task> Insert { get; init; }
            = (_, _) => Task.CompletedTask;
        public Func<RoleMenuSettings, CancellationToken, Task> Upsert { get; init; }
            = (_, _) => Task.CompletedTask;
        public Func<ObjectId, string, CancellationToken, Task<RoleMenuSettings?>> GetById
        { get; init; } = (_, _, _) => Task.FromResult<RoleMenuSettings?>(null);
        public Func<string, int, CancellationToken, Task<List<RoleMenuSettings>>> GetByGuild
        { get; init; } = (_, _, _) => Task.FromResult(new List<RoleMenuSettings>());
        public Func<ObjectId, string, CancellationToken, Task<bool>> Delete { get; init; }
            = (_, _, _) => Task.FromResult(false);

        public Task InsertAsync(
            RoleMenuSettings settings,
            CancellationToken cancellationToken)
            => Insert(settings, cancellationToken);

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

        public Task<bool> DeleteAsync(
            ObjectId id,
            string guildId,
            CancellationToken cancellationToken)
            => Delete(id, guildId, cancellationToken);
    }
}
