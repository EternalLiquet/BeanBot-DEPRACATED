using BeanBot.Discord.ReactionRoles;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class ReactionRoleServiceCacheConcurrencyTests
{
    [Fact]
    public async Task DeletedPanel_StopsInFlightFallbackFromRestoringStaleCache()
    {
        var setting = CreateRoleSettings("42");
        var fallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        ReactionRoleSettings? current = setting;
        var store = new CoordinatedReactionRoleSettingsStore
        {
            GetByMessageId = async (_, token) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    fallbackStarted.SetResult();
                    await releaseFallback.Task.WaitAsync(token);
                }
                return current;
            },
            Delete = (_, _) =>
            {
                current = null;
                return Task.FromResult(true);
            }
        };
        await using var service = CreateService(store, cacheCapacity: 2);
        var staleRead = service.GetCachedRoleSettingAsync(42, CancellationToken.None);
        await fallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(await service.DeleteSavedPanelAsync(1, 2, 42, CancellationToken.None));
        releaseFallback.SetResult();

        Assert.Null(await staleRead);
        Assert.Null(await service.GetCachedRoleSettingAsync(42, CancellationToken.None));
        Assert.Equal(0, service.CachedRoleSettingsCount);
    }

    [Fact]
    public async Task InitialPreload_DoesNotEvictSettingPersistedWhileQueryIsInFlight()
    {
        var preloadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreload = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CoordinatedReactionRoleSettingsStore
        {
            GetRecent = async (_, _, cancellationToken) =>
            {
                preloadStarted.TrySetResult();
                await releasePreload.Task.WaitAsync(cancellationToken);
                return [CreateRoleSettings("1"), CreateRoleSettings("2")];
            }
        };
        await using var service = CreateService(store, cacheCapacity: 2);
        var initialLookup = service.GetCachedRoleSettingAsync(1UL, CancellationToken.None);
        await preloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var persisted = CreateRoleSettings("99");

        await service.PersistRoleSettingsAsync(persisted, CancellationToken.None);
        releasePreload.TrySetResult();
        await initialLookup;
        var cached = await service.GetCachedRoleSettingAsync(99UL, CancellationToken.None);

        Assert.Same(persisted, cached);
        Assert.Equal(2, service.CachedRoleSettingsCount);
        Assert.Equal(0, store.GetByMessageIdCallCount);
    }

    private static ReactionRoleService CreateService(
        IReactionRoleSettingsStore store,
        int cacheCapacity)
        => new(
            new ReactionRoleRepository(store, NullLogger<ReactionRoleRepository>.Instance),
            client: null,
            TimeSpan.FromSeconds(1),
            NullLogger<ReactionRoleService>.Instance,
            cacheCapacity,
            CancellationToken.None);

    private static ReactionRoleSettings CreateRoleSettings(string messageId)
        => new([], "1", "2", messageId);

    private sealed class CoordinatedReactionRoleSettingsStore : IReactionRoleSettingsStore
    {
        public Func<DateTime, int, CancellationToken, Task<List<ReactionRoleSettings>>> GetRecent { get; set; }
            = (_, _, _) => Task.FromResult(new List<ReactionRoleSettings>());

        public int GetByMessageIdCallCount { get; private set; }
        public Func<string, CancellationToken, Task<ReactionRoleSettings?>> GetByMessageId { get; set; }
            = (_, _) => Task.FromResult<ReactionRoleSettings?>(null);
        public Func<ReactionRoleSettings, CancellationToken, Task<bool>> Delete { get; set; }
            = (_, _) => Task.FromResult(false);

        public Task InsertAsync(ReactionRoleSettings roleSettings, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<List<ReactionRoleSettings>> GetRecentAsync(
            DateTime oldestLastAccessedUtc,
            int limit,
            CancellationToken cancellationToken)
            => GetRecent(oldestLastAccessedUtc, limit, cancellationToken);

        public Task<ReactionRoleSettings?> GetByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken)
        {
            GetByMessageIdCallCount++;
            return GetByMessageId(messageId, cancellationToken);
        }

        public Task<bool> DeleteBindingAsync(ReactionRoleSettings settings, CancellationToken cancellationToken)
            => Delete(settings, cancellationToken);
    }
}
