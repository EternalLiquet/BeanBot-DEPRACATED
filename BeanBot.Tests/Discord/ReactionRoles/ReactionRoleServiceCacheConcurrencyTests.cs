using BeanBot.Discord.ReactionRoles;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class ReactionRoleServiceCacheConcurrencyTests
{
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
            return Task.FromResult<ReactionRoleSettings?>(null);
        }
    }
}
