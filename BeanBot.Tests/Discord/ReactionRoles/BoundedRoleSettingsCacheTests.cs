using BeanBot.Discord.ReactionRoles;
using BeanBot.Persistence.Models;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class BoundedRoleSettingsCacheTests
{
    [Fact]
    public void Set_EvictsLeastRecentlyUsedEntryAtCapacity()
    {
        var cache = new BoundedRoleSettingsCache(2);
        var first = CreateRoleSettings("1");
        var second = CreateRoleSettings("2");
        var third = CreateRoleSettings("3");

        cache.Set(first);
        cache.Set(second);
        Assert.True(cache.TryGet("1", out _));
        cache.Set(third);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("1", out var cachedFirst));
        Assert.False(cache.TryGet("2", out _));
        Assert.True(cache.TryGet("3", out var cachedThird));
        Assert.Same(first, cachedFirst);
        Assert.Same(third, cachedThird);
    }

    [Fact]
    public void Set_UpdatingExistingEntryRefreshesRecencyWithoutGrowingBookkeeping()
    {
        var cache = new BoundedRoleSettingsCache(2);
        cache.Set(CreateRoleSettings("1"));
        cache.Set(CreateRoleSettings("2"));
        var replacement = CreateRoleSettings("1");

        cache.Set(replacement);
        cache.Set(CreateRoleSettings("3"));

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("1", out var cached));
        Assert.Same(replacement, cached);
        Assert.False(cache.TryGet("2", out _));
    }

    [Fact]
    public async Task ConcurrentSets_NeverExceedCapacityOrCorruptCache()
    {
        const int capacity = 8;
        var cache = new BoundedRoleSettingsCache(capacity);

        await Task.WhenAll(Enumerable.Range(1, 200).Select(index => Task.Run(() =>
        {
            cache.Set(CreateRoleSettings(index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.InRange(cache.Count, 0, capacity);
        })));

        Assert.Equal(capacity, cache.Count);
    }

    private static RoleSettings CreateRoleSettings(string messageId)
        => new([], "1", "2", messageId);
}
