using System.Reflection;
using BeanBot.Discord.ReactionRoles;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class RoleReactServiceTests
{
    [Fact]
    public async Task DisposeAsync_DrainsTrackedHandlersBeforeDisposingCacheLock()
    {
        var service = CreateService(TimeSpan.FromSeconds(1));
        var cacheLock = GetCacheLock(service);
        var handlerCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = service.TrackHandlerAsync(_ => handlerCompletion.Task);

        var firstDispose = service.DisposeAsync().AsTask();
        var secondDispose = service.DisposeAsync().AsTask();

        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);
        handlerCompletion.SetResult();

        await handler;
        await firstDispose;
        await secondDispose;

        Assert.Throws<ObjectDisposedException>(() => cacheLock.Wait(0));
    }

    [Fact]
    public async Task DisposeAsync_DrainTimeoutLeavesCacheLockForProcessExit()
    {
        var service = CreateService(TimeSpan.FromMilliseconds(20));
        var cacheLock = GetCacheLock(service);
        var handlerCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = service.TrackHandlerAsync(_ => handlerCompletion.Task);

        await service.DisposeAsync();

        Assert.True(cacheLock.Wait(0));
        cacheLock.Release();
        handlerCompletion.SetResult();
        await handler;
    }

    [Fact]
    public async Task GetCachedRoleSettingAsync_CachesSuccessfulFallbackLookup()
    {
        var expected = CreateRoleSettings("42");
        var store = new FakeRoleSettingsStore
        {
            GetByMessageId = (messageId, _) =>
            {
                Assert.Equal("42", messageId);
                return Task.FromResult<RoleSettings?>(expected);
            }
        };
        await using var service = CreateService(TimeSpan.FromSeconds(1), store);

        var first = await service.GetCachedRoleSettingAsync(42UL, CancellationToken.None);
        var second = await service.GetCachedRoleSettingAsync(42UL, CancellationToken.None);

        Assert.Same(expected, first);
        Assert.Same(expected, second);
        Assert.Equal(1, store.GetRecentCallCount);
        Assert.Equal(1, store.GetByMessageIdCallCount);
    }

    [Fact]
    public async Task GetCachedRoleSettingAsync_PreloadUsesCacheCapacity()
    {
        var newest = CreateRoleSettings("42");
        var older = CreateRoleSettings("41");
        var store = new FakeRoleSettingsStore
        {
            GetRecent = (_, limit, _) =>
            {
                Assert.Equal(2, limit);
                return Task.FromResult(new List<RoleSettings> { newest, older });
            }
        };
        await using var service = CreateService(
            TimeSpan.FromSeconds(1),
            store,
            cacheCapacity: 2);

        var actual = await service.GetCachedRoleSettingAsync(42UL, CancellationToken.None);

        Assert.Same(newest, actual);
        Assert.Equal(2, service.CachedRoleSettingsCount);
        Assert.Equal(1, store.GetRecentCallCount);
        Assert.Equal(0, store.GetByMessageIdCallCount);
    }

    [Fact]
    public async Task GetCachedRoleSettingAsync_EvictedSettingReloadsFromStore()
    {
        var store = new FakeRoleSettingsStore
        {
            GetByMessageId = (messageId, _) =>
                Task.FromResult<RoleSettings?>(CreateRoleSettings(messageId))
        };
        await using var service = CreateService(
            TimeSpan.FromSeconds(1),
            store,
            cacheCapacity: 2);

        await service.GetCachedRoleSettingAsync(1UL, CancellationToken.None);
        await service.GetCachedRoleSettingAsync(2UL, CancellationToken.None);
        await service.GetCachedRoleSettingAsync(1UL, CancellationToken.None);
        await service.GetCachedRoleSettingAsync(3UL, CancellationToken.None);
        var reloaded = await service.GetCachedRoleSettingAsync(2UL, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal("2", reloaded.MessageId);
        Assert.Equal(2, service.CachedRoleSettingsCount);
        Assert.Equal(4, store.GetByMessageIdCallCount);
    }

    [Fact]
    public async Task GetCachedRoleSettingAsync_DoesNotNegativeCacheMissingSetting()
    {
        var store = new FakeRoleSettingsStore();
        await using var service = CreateService(TimeSpan.FromSeconds(1), store);

        var first = await service.GetCachedRoleSettingAsync(42UL, CancellationToken.None);
        var second = await service.GetCachedRoleSettingAsync(42UL, CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, store.GetByMessageIdCallCount);
    }

    [Fact]
    public async Task GetCachedRoleSettingAsync_DoesNotCacheInfrastructureFailure()
    {
        var expected = CreateRoleSettings("42");
        var failure = new InvalidOperationException("database unavailable");
        var store = new FakeRoleSettingsStore();
        store.GetByMessageId = (_, _) => store.GetByMessageIdCallCount == 1
            ? Task.FromException<RoleSettings?>(failure)
            : Task.FromResult<RoleSettings?>(expected);
        await using var service = CreateService(TimeSpan.FromSeconds(1), store);

        var actualFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetCachedRoleSettingAsync(42UL, CancellationToken.None));
        var recovered = await service.GetCachedRoleSettingAsync(42UL, CancellationToken.None);

        Assert.Same(failure, actualFailure);
        Assert.Same(expected, recovered);
        Assert.Equal(2, store.GetByMessageIdCallCount);
    }

    [Fact]
    public async Task GetCachedRoleSettingAsync_RetriesFailedInitialCacheLoad()
    {
        var expected = CreateRoleSettings("42");
        var failure = new InvalidOperationException("database unavailable");
        var store = new FakeRoleSettingsStore();
        store.GetRecent = (_, _, _) => store.GetRecentCallCount == 1
            ? Task.FromException<List<RoleSettings>>(failure)
            : Task.FromResult(new List<RoleSettings> { expected });
        await using var service = CreateService(TimeSpan.FromSeconds(1), store);

        var actualFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetCachedRoleSettingAsync(42UL, CancellationToken.None));
        var recovered = await service.GetCachedRoleSettingAsync(42UL, CancellationToken.None);

        Assert.Same(failure, actualFailure);
        Assert.Same(expected, recovered);
        Assert.Equal(2, store.GetRecentCallCount);
        Assert.Equal(0, store.GetByMessageIdCallCount);
    }

    [Fact]
    public async Task PersistRoleSettingsAsync_CachesSuccessfulInsert()
    {
        var settings = CreateRoleSettings("42");
        var store = new FakeRoleSettingsStore();
        await using var service = CreateService(TimeSpan.FromSeconds(1), store);

        await service.PersistRoleSettingsAsync(settings, CancellationToken.None);
        var cached = await service.GetCachedRoleSettingAsync(42UL, CancellationToken.None);

        Assert.Same(settings, cached);
        Assert.Equal(1, service.CachedRoleSettingsCount);
        Assert.Equal(0, store.GetByMessageIdCallCount);
    }

    [Fact]
    public async Task PersistRoleSettingsAsync_DoesNotCacheFailedInsert()
    {
        var settings = CreateRoleSettings("42");
        var failure = new InvalidOperationException("database unavailable");
        var store = new FakeRoleSettingsStore
        {
            Insert = (_, _) => Task.FromException(failure)
        };
        await using var service = CreateService(TimeSpan.FromSeconds(1), store);

        var actualFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PersistRoleSettingsAsync(settings, CancellationToken.None));
        var cached = await service.GetCachedRoleSettingAsync(42UL, CancellationToken.None);

        Assert.Same(failure, actualFailure);
        Assert.Null(cached);
        Assert.Equal(1, store.GetByMessageIdCallCount);
    }

    [Fact]
    public async Task DisposeAsync_CancelsTrackedRepositoryWorkBeforeDrain()
    {
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeRoleSettingsStore
        {
            GetRecent = async (_, _, cancellationToken) =>
            {
                operationStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
        };
        var service = CreateService(TimeSpan.FromSeconds(1), store);
        var cacheLock = GetCacheLock(service);
        var handler = service.TrackHandlerAsync(cancellationToken =>
            service.GetCachedRoleSettingAsync(42UL, cancellationToken));
        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler);
        Assert.Throws<ObjectDisposedException>(() => cacheLock.Wait(0));
    }

    [Fact]
    public async Task TrackHandlerAsync_AfterShutdownDoesNotStartHandler()
    {
        var service = CreateService(TimeSpan.FromSeconds(1));
        await service.DisposeAsync();
        var started = false;

        await service.TrackHandlerAsync(_ =>
        {
            started = true;
            return Task.CompletedTask;
        });

        Assert.False(started);
    }

    [Fact]
    public async Task TrackHandlerAsync_AfterApplicationStoppingDoesNotStartHandler()
    {
        using var applicationStopping = new CancellationTokenSource();
        await using var service = CreateService(
            TimeSpan.FromSeconds(1),
            applicationStopping: applicationStopping.Token);
        applicationStopping.Cancel();
        var started = false;

        await service.TrackHandlerAsync(_ =>
        {
            started = true;
            return Task.CompletedTask;
        });

        Assert.False(started);
    }

    private static RoleReactService CreateService(
        TimeSpan shutdownDrainTimeout,
        FakeRoleSettingsStore? store = null,
        int cacheCapacity = 256,
        CancellationToken applicationStopping = default)
    {
        return new RoleReactService(
            new RoleReactRepository(
                store ?? new FakeRoleSettingsStore(),
                NullLogger<RoleReactRepository>.Instance),
            client: null,
            shutdownDrainTimeout,
            NullLogger<RoleReactService>.Instance,
            cacheCapacity,
            applicationStopping);
    }

    private static RoleSettings CreateRoleSettings(string messageId)
        => new([], "1", "2", messageId);

    private static SemaphoreSlim GetCacheLock(RoleReactService service)
    {
        var cacheLockField = typeof(RoleReactService).GetField(
            "_cacheLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<SemaphoreSlim>(cacheLockField?.GetValue(service));
    }

    private sealed class FakeRoleSettingsStore : IRoleSettingsStore
    {
        public Func<RoleSettings, CancellationToken, Task> Insert { get; set; }
            = (_, _) => Task.CompletedTask;
        public Func<DateTime, int, CancellationToken, Task<List<RoleSettings>>> GetRecent { get; set; }
            = (_, _, _) => Task.FromResult(new List<RoleSettings>());
        public Func<string, CancellationToken, Task<RoleSettings?>> GetByMessageId { get; set; }
            = (_, _) => Task.FromResult<RoleSettings?>(null);

        public int GetRecentCallCount { get; private set; }
        public int GetByMessageIdCallCount { get; private set; }

        public Task InsertAsync(RoleSettings roleSettings, CancellationToken cancellationToken)
            => Insert(roleSettings, cancellationToken);

        public Task<List<RoleSettings>> GetRecentAsync(
            DateTime oldestLastAccessedUtc,
            int limit,
            CancellationToken cancellationToken)
        {
            GetRecentCallCount++;
            return GetRecent(oldestLastAccessedUtc, limit, cancellationToken);
        }

        public Task<RoleSettings?> GetByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken)
        {
            GetByMessageIdCallCount++;
            return GetByMessageId(messageId, cancellationToken);
        }
    }
}
