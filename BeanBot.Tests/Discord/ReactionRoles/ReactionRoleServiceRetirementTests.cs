using BeanBot.Discord.ReactionRoles;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
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
            cancellationToken => service.DeleteRoleSettingAsync(CreateSettings(), cancellationToken),
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
            new ReactionRoleSettings([new RoleEmotePair("4", "5")], "99", "2", "3"),
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
            service.DeleteRoleSettingAsync(CreateSettings(), CancellationToken.None));
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
            cancellationToken => service.DeleteRoleSettingAsync(CreateSettings(), cancellationToken),
            CancellationToken.None);

        Assert.False(retirement.IsCompleted);
        releaseLookup.TrySetResult();
        Assert.NotNull(await cacheFill);
        Assert.True(await retirement);

        Assert.Equal(0, service.CachedRoleSettingsCount);
    }

    [Fact]
    public async Task RepeatedRetirement_DoesNotDeleteReplacementOrRunDuplicateMutation()
    {
        var original = new ReactionRoleSettings(
            [new RoleEmotePair("4", "5")], "1", "2", "3")
        { Id = ObjectId.GenerateNewId() };
        var replacement = new ReactionRoleSettings(
            [new RoleEmotePair("6", "7")], "1", "2", "3")
        { Id = ObjectId.GenerateNewId() };
        var store = new RetirementStore { Settings = original };
        await using var service = CreateService(store);

        var first = await service.RunSettingsRetirementAsync(3,
            token => service.DeleteRoleSettingAsync(original, token), CancellationToken.None);
        store.Settings = replacement;
        var second = await service.RunSettingsRetirementAsync(3,
            token => service.DeleteRoleSettingAsync(original, token), CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Same(replacement, store.Settings);
    }

    [Fact]
    public async Task RetirementIgnoringCancellation_RemainsOwnedAfterDrainTimeout()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ReactionRoleService(
            new ReactionRoleRepository(new RetirementStore(),
                NullLogger<ReactionRoleRepository>.Instance),
            client: null,
            TimeSpan.FromMilliseconds(50),
            NullLogger<ReactionRoleService>.Instance,
            cacheCapacity: 8,
            CancellationToken.None);
        var operation = service.RunSettingsRetirementAsync(3,
            async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return true;
            }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await service.DisposeAsync();
        Assert.True(service.HasPendingOperations);
        Assert.False(operation.IsCompleted);
        release.TrySetResult();
        Assert.True(await operation);
        Assert.False(service.HasPendingOperations);
    }

    [Fact]
    public async Task ConfirmationClaim_ConsumesOnePreviewUntilExpiry()
    {
        var store = new RetirementStore();
        await using var service = CreateService(store);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var expiry = now.AddMinutes(10).ToUnixTimeSeconds();

        Assert.True(service.TryClaimLegacyRetirement("confirmation-a", expiry, now));
        Assert.False(service.TryClaimLegacyRetirement("confirmation-a", expiry, now));
        Assert.True(service.TryClaimLegacyRetirement("confirmation-b", expiry, now));
        Assert.False(service.TryClaimLegacyRetirement("confirmation-a", expiry,
            now.AddMinutes(10)));
        Assert.True(service.TryClaimLegacyRetirement("confirmation-a",
            now.AddMinutes(20).ToUnixTimeSeconds(), now.AddMinutes(11)));
    }

    [Fact]
    public async Task ConfirmationClaims_AreBoundedAndClosedOnShutdown()
    {
        var service = CreateService(new RetirementStore());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var expiry = now.AddMinutes(10).ToUnixTimeSeconds();
        for (var index = 0; index < 128; index++)
        {
            Assert.True(service.TryClaimLegacyRetirement(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                expiry, now));
        }
        Assert.False(service.TryClaimLegacyRetirement("overflow", expiry, now));
        await service.DisposeAsync();
        Assert.False(service.TryClaimLegacyRetirement("after-stop", expiry, now));
    }

    [Fact]
    public async Task GuildPage_ForwardsBoundedQueryWithoutLoadingCache()
    {
        var store = new RetirementStore { Settings = CreateSettings() };
        await using var service = CreateService(store);

        var page = await service.GetGuildPageAsync(1, null, newer: false, 26,
            CancellationToken.None);

        Assert.Single(page);
        Assert.Equal(0, service.CachedRoleSettingsCount);
        Assert.Equal(26, store.LastPageLimit);
    }

    [Fact]
    public async Task SaveRoleSettings_RejectsMessageOutsideGuildTextChannel()
    {
        await using var service = CreateService(new RetirementStore());
        var message = System.Reflection.DispatchProxy.Create<IMessage,
            MissingChannelMessageProxy>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveRoleSettings([new RoleEmotePair("4", "5")], message));
    }

    [Fact]
    public async Task ReactionCallbacks_WithoutConnectedBotDoNotAdmitRoleWork()
    {
        await using var service = CreateService(new RetirementStore());

        await service.HandleReact(default, default, null!);
        await service.HandleRemoveReact(default, default, null!);
        Assert.False(service.HasPendingOperations);
    }

    [Fact]
    public async Task DisposeTwice_DoesNotRaceRetirementLeaseCleanup()
    {
        var service = CreateService(new RetirementStore());

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.False(service.HasPendingOperations);
    }

    public class MissingChannelMessageProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod,
            object?[]? args)
            => targetMethod?.Name == "get_Channel"
                ? null
                : throw new NotSupportedException(targetMethod?.Name);
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
        public int LastPageLimit { get; private set; }
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
            return Task.FromResult<List<ReactionRoleSettings>>([]);
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

        public Task<List<ReactionRoleSettings>> GetGuildPageAsync(
            string guildId, ObjectId? cursor, bool newer, int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPageLimit = limit;
            return Task.FromResult<List<ReactionRoleSettings>>(Settings?.GuildId == guildId
                ? [Settings]
                : []);
        }

        public Task<ReactionRoleSettings?> GetByBindingAsync(
            string guildId, string channelId, string messageId,
            CancellationToken cancellationToken)
            => Task.FromResult<ReactionRoleSettings?>(Settings?.GuildId == guildId
                && Settings.ChannelId == channelId
                && Settings.MessageId == messageId ? Settings : null);

        public Task<bool> DeleteBindingAsync(
            ReactionRoleSettings expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DeleteException is not null)
            {
                return Task.FromException<bool>(DeleteException);
            }
            if (Settings?.Id != expected.Id || Settings.GuildId != expected.GuildId
                || Settings.ChannelId != expected.ChannelId
                || Settings.MessageId != expected.MessageId)
            {
                return Task.FromResult(false);
            }
            Settings = null;
            return Task.FromResult(true);
        }
    }
}
