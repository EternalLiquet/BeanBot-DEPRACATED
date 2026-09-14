using BeanBot.Discord.ReactionRoles;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class ReactionRoleServiceRetirementTests
{
    [Fact]
    public async Task DeleteRoleSettingAsync_RemovesPersistedRecordAndCachedSetting()
    {
        var store = new RetirementStore { Settings = CreateSettings() };
        await using var service = CreateService(store);

        Assert.NotNull(await service.GetFreshRoleSettingAsync(3UL, CancellationToken.None));
        Assert.Equal(1, service.CachedRoleSettingsCount);

        var deleted = await service.RunSettingsRetirementAsync(
            3UL,
            cancellationToken => service.DeleteRoleSettingAsync(3UL, 1UL, cancellationToken),
            CancellationToken.None);

        Assert.True(deleted);
        Assert.Null(store.Settings);
        Assert.Equal(0, service.CachedRoleSettingsCount);
        Assert.Null(await service.GetFreshRoleSettingAsync(3UL, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteRoleSettingAsync_WrongGuildKeepsPersistedAndCachedSetting()
    {
        var store = new RetirementStore { Settings = CreateSettings() };
        await using var service = CreateService(store);
        var expected = await service.GetFreshRoleSettingAsync(3UL, CancellationToken.None);

        var deleted = await service.DeleteRoleSettingAsync(
            3UL,
            99UL,
            CancellationToken.None);
        var cached = await service.GetCachedRoleSettingAsync(3UL, CancellationToken.None);

        Assert.False(deleted);
        Assert.Same(expected, cached);
        Assert.NotNull(store.Settings);
        Assert.Equal(1, service.CachedRoleSettingsCount);
    }

    [Fact]
    public async Task DeleteRoleSettingAsync_FailureDoesNotEvictUnconfirmedSetting()
    {
        var store = new RetirementStore
        {
            Settings = CreateSettings(),
            DeleteException = new InvalidOperationException("Mongo unavailable")
        };
        await using var service = CreateService(store);
        var expected = await service.GetFreshRoleSettingAsync(3UL, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteRoleSettingAsync(3UL, 1UL, CancellationToken.None));
        var cached = await service.GetCachedRoleSettingAsync(3UL, CancellationToken.None);

        Assert.Same(expected, cached);
        Assert.Equal(1, service.CachedRoleSettingsCount);
    }

    [Fact]
    public async Task Retirement_WaitsForConcurrentCacheFillThenEvictsIt()
    {
        var lookupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RetirementStore
        {
            Settings = CreateSettings(),
            GetByMessageId = async (_, cancellationToken) =>
            {
                lookupStarted.TrySetResult();
                await releaseLookup.Task.WaitAsync(cancellationToken);
                return CreateSettings();
            }
        };
        await using var service = CreateService(store);

        var cacheFill = service.GetCachedRoleSettingAsync(3UL, CancellationToken.None);
        await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var retirement = service.RunSettingsRetirementAsync(
            3UL,
            cancellationToken => service.DeleteRoleSettingAsync(3UL, 1UL, cancellationToken),
            CancellationToken.None);

        Assert.False(retirement.IsCompleted);
        releaseLookup.TrySetResult();
        Assert.NotNull(await cacheFill);
        Assert.True(await retirement);

        Assert.Equal(0, service.CachedRoleSettingsCount);
    }

    private static ReactionRoleService CreateService(IReactionRoleSettingsStore store)
        => new(
            new ReactionRoleRepository(store, NullLogger<ReactionRoleRepository>.Instance),
            client: null,
            TimeSpan.FromSeconds(1),
            NullLogger<ReactionRoleService>.Instance,
            cacheCapacity: 8,
            CancellationToken.None);

    private static ReactionRoleSettings CreateSettings()
        => new([new RoleEmotePair("4", "5")], "1", "2", "3");

    private sealed class RetirementStore : IReactionRoleSettingsStore
    {
        public ReactionRoleSettings? Settings { get; set; }
        public Exception? DeleteException { get; set; }
        public Func<string, CancellationToken, Task<ReactionRoleSettings?>>? GetByMessageId { get; set; }

        public Task InsertAsync(
            ReactionRoleSettings roleSettings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Settings = roleSettings;
            return Task.CompletedTask;
        }

        public Task<List<ReactionRoleSettings>> GetRecentAsync(
            DateTime oldestLastAccessedUtc,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new List<ReactionRoleSettings>());
        }

        public Task<ReactionRoleSettings?> GetByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetByMessageId is null
                ? Task.FromResult(Settings?.MessageId == messageId ? Settings : null)
                : GetByMessageId(messageId, cancellationToken);
        }

        public Task<bool> DeleteByMessageIdAndGuildIdAsync(
            string messageId,
            string guildId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DeleteException is not null)
            {
                return Task.FromException<bool>(DeleteException);
            }

            if (Settings?.MessageId != messageId || Settings.GuildId != guildId)
            {
                return Task.FromResult(false);
            }

            Settings = null;
            return Task.FromResult(true);
        }
    }
}
