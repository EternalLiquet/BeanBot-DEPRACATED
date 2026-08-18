using BeanBot.Repository;
using BeanBot.Services;

using MongoDB.Driver;
using Microsoft.Extensions.Logging.Abstractions;

using System.Reflection;

using Xunit;

namespace BeanBot.Tests.Services;

public class RoleReactServiceTests
{
    [Fact]
    public async Task DisposeAsync_DrainsTrackedHandlersBeforeDisposingCacheLock()
    {
        var service = CreateService(TimeSpan.FromSeconds(1));
        var cacheLock = GetCacheLock(service);
        var handlerCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = service.TrackHandlerAsync(() => handlerCompletion.Task);

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
        var handler = service.TrackHandlerAsync(() => handlerCompletion.Task);

        await service.DisposeAsync();

        Assert.True(cacheLock.Wait(0));
        cacheLock.Release();
        handlerCompletion.SetResult();
        await handler;
    }

    private static RoleReactService CreateService(TimeSpan shutdownDrainTimeout)
    {
        var database = new MongoClient("mongodb://localhost:27017")
            .GetDatabase("BeanBotNullableTests");
        return new RoleReactService(
            new RoleReactRepository(database, NullLogger<RoleReactRepository>.Instance),
            client: null,
            shutdownDrainTimeout,
            NullLogger<RoleReactService>.Instance);
    }

    private static SemaphoreSlim GetCacheLock(RoleReactService service)
    {
        var cacheLockField = typeof(RoleReactService).GetField(
            "_cacheLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<SemaphoreSlim>(cacheLockField?.GetValue(service));
    }
}
