using BeanBot.Discord.Events;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.ReactionRoles;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public sealed class RolePanelDeletionHandlerTests
{
    [Fact]
    public async Task UncachedBulkIds_DeleteBothPanelKindsOnce_AndLeaveUnrelatedSettings()
    {
        var legacy = new LegacyStore { Current = new ReactionRoleSettings([], "1", "2", "10") };
        var menus = new MenuStore();
        var target = Menu("11");
        var unrelated = Menu("12");
        menus.Settings.AddRange([target, unrelated]);
        await using var service = CreateLegacyService(legacy);
        using var client = new DiscordSocketClient();
        var handler = CreateHandler(client, service, menus);

        // Gateway cache misses still carry message IDs. No deleted message or author is fetched.
        await handler.HandleDeletedMessageIdsAsync(1, 2, [10, 11, 10, 99]);
        await handler.HandleDeletedMessageIdsAsync(1, 2, [10, 11]);

        Assert.Null(legacy.Current);
        Assert.Equal(1, legacy.Deletes);
        Assert.Single(menus.Settings);
        Assert.Same(unrelated, menus.Settings[0]);
        Assert.Equal(1, menus.Deletes);
    }

    [Fact]
    public async Task WrongContextAndLegacyDatabaseFailure_DoNotDeleteUnrelatedPanel()
    {
        var legacy = new LegacyStore { Current = new ReactionRoleSettings([], "1", "2", "10") };
        var menus = new MenuStore();
        menus.Settings.Add(Menu("10"));
        await using var service = CreateLegacyService(legacy);
        using var client = new DiscordSocketClient();
        var handler = CreateHandler(client, service, menus);

        await handler.HandleDeletedMessageIdsAsync(1, 9, [10]);
        Assert.NotNull(legacy.Current);
        Assert.Single(menus.Settings);
        legacy.FailDelete = true;
        await handler.HandleDeletedMessageIdsAsync(1, 2, [10]);

        Assert.NotNull(legacy.Current);
        Assert.Empty(menus.Settings);
        Assert.Equal(1, menus.Deletes);
    }

    [Fact]
    public async Task Shutdown_DrainsTrackedDeletionBeforeServiceDisposal()
    {
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacy = new LegacyStore
        {
            Read = async token =>
            {
                readStarted.SetResult();
                await releaseRead.Task.WaitAsync(token);
                return null;
            }
        };
        var service = CreateLegacyService(legacy);
        using var client = new DiscordSocketClient();
        var handler = CreateHandler(client, service, new MenuStore());
        var eventTask = handler.HandleDeletedMessageIdsAsync(1, 2, [10]);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(service.HasPendingOperations);

        var shutdown = service.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => eventTask);
        await shutdown;
        Assert.False(service.HasPendingOperations);
    }

    [Fact]
    public async Task Shutdown_KeepsCancellationIgnoringCleanupTrackedUntilItSettles()
    {
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacy = new LegacyStore
        {
            Read = async _ =>
            {
                readStarted.SetResult();
                await releaseRead.Task;
                return null;
            }
        };
        var service = CreateLegacyService(legacy);
        using var client = new DiscordSocketClient();
        var handler = CreateHandler(client, service, new MenuStore());
        var eventTask = handler.HandleDeletedMessageIdsAsync(1, 2, [10]);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdown = service.DisposeAsync().AsTask();
        Assert.True(service.HasPendingOperations);
        Assert.False(shutdown.IsCompleted);
        releaseRead.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => eventTask);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.HasPendingOperations);
    }

    private static ReactionRoleHandler CreateHandler(
        DiscordSocketClient client, ReactionRoleService service, MenuStore menus)
    {
        var repository = new RoleMenuRepository(menus, NullLogger<RoleMenuRepository>.Instance);
        var roleMenus = new RoleMenuInteractionService(
            repository, new RoleMenuDraftRegistry(), new RoleMenuMutationCoordinator(),
            new InteractionExecutionContext());
        return new ReactionRoleHandler(client, service, roleMenus,
            NullLogger<ReactionRoleHandler>.Instance);
    }

    private static ReactionRoleService CreateLegacyService(LegacyStore store)
        => new(new ReactionRoleRepository(store, NullLogger<ReactionRoleRepository>.Instance),
            client: null, TimeSpan.FromSeconds(1), NullLogger<ReactionRoleService>.Instance,
            cacheCapacity: 4, CancellationToken.None);

    private static RoleMenuSettings Menu(string messageId)
        => new(ObjectId.GenerateNewId(), "1", "2", messageId, "Games", "", ["3"],
            RoleMenuSelectionMode.Multiple);

    private sealed class LegacyStore : IReactionRoleSettingsStore
    {
        internal ReactionRoleSettings? Current { get; set; }
        internal int Deletes { get; private set; }
        internal bool FailDelete { get; set; }
        internal Func<CancellationToken, Task<ReactionRoleSettings?>>? Read { get; set; }

        public Task InsertAsync(ReactionRoleSettings settings, CancellationToken token)
            => Task.CompletedTask;
        public Task<List<ReactionRoleSettings>> GetRecentAsync(DateTime cutoff, int limit, CancellationToken token)
            => Task.FromResult(new List<ReactionRoleSettings>());
        public Task<ReactionRoleSettings?> GetByMessageIdAsync(string messageId, CancellationToken token)
            => Task.FromResult(Current?.MessageId == messageId ? Current : null);
        public Task<ReactionRoleSettings?> GetByBindingAsync(
            string guildId, string channelId, string messageId, CancellationToken token)
            => Read is not null ? Read(token) : Task.FromResult(
                Current?.GuildId == guildId && Current.ChannelId == channelId
                && Current.MessageId == messageId ? Current : null);
        public Task<bool> DeleteBindingAsync(ReactionRoleSettings settings, CancellationToken token)
        {
            if (FailDelete)
            {
                return Task.FromException<bool>(new InvalidOperationException("database unavailable"));
            }

            if (Current != settings)
            {
                return Task.FromResult(false);
            }

            Current = null;
            Deletes++;
            return Task.FromResult(true);
        }
    }

    private sealed class MenuStore : IRoleMenuStore
    {
        internal List<RoleMenuSettings> Settings { get; } = [];
        internal int Deletes { get; private set; }
        public Task UpsertAsync(RoleMenuSettings settings, CancellationToken token)
            => Task.CompletedTask;
        public Task<RoleMenuSettings?> GetByIdAsync(ObjectId id, string guildId, CancellationToken token)
            => Task.FromResult(Settings.FirstOrDefault(item => item.Id == id && item.GuildId == guildId));
        public Task<List<RoleMenuSettings>> GetByGuildAsync(string guildId, int limit, CancellationToken token)
            => Task.FromResult(Settings.Where(item => item.GuildId == guildId).Take(limit).ToList());
        public Task<List<RoleMenuSettings>> GetByMessageAsync(
            string guildId, string channelId, string messageId, int limit, CancellationToken token)
            => Task.FromResult(Settings.Where(item => item.GuildId == guildId
                && item.ChannelId == channelId && item.MessageId == messageId).Take(limit).ToList());
        public Task<List<RoleMenuSettings>> GetPageAsync(
            string guildId, RoleMenuPageCursor? cursor, int limit, CancellationToken token)
            => GetByGuildAsync(guildId, limit, token);
        public Task<bool> DeleteAsync(ObjectId id, string guildId, CancellationToken token)
            => Task.FromResult(Settings.RemoveAll(item => item.Id == id && item.GuildId == guildId) != 0);
        public Task<bool> DeleteBindingAsync(RoleMenuSettings settings, CancellationToken token)
        {
            var removed = Settings.RemoveAll(item => item.Id == settings.Id
                && item.GuildId == settings.GuildId && item.ChannelId == settings.ChannelId
                && item.MessageId == settings.MessageId && item.UpdatedAtUtc == settings.UpdatedAtUtc) != 0;
            if (removed)
            {
                Deletes++;
            }

            return Task.FromResult(removed);
        }
    }
}
