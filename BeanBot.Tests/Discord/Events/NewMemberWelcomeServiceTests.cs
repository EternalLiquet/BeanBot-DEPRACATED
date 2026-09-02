using BeanBot.Configuration;
using BeanBot.Discord.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Events;

public class NewMemberWelcomeServiceTests
{
    [Fact]
    public async Task TryEnqueue_DeliversAcceptedHumanMemberOnce()
    {
        var delivered = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new StubDelivery((userId, _) =>
        {
            delivered.TrySetResult(userId);
            return Task.CompletedTask;
        });
        await using var service = CreateService(delivery);
        service.Start();

        var accepted = service.TryEnqueue(42, isBot: false);

        Assert.True(accepted);
        Assert.Equal((ulong)42, await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(() => service.OutstandingCount == 0);
    }

    [Fact]
    public async Task TryEnqueue_IgnoresBotJoin()
    {
        var calls = 0;
        var delivery = new StubDelivery((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        await using var service = CreateService(delivery);
        service.Start();

        var accepted = service.TryEnqueue(42, isBot: true);
        await Task.Delay(25);

        Assert.False(accepted);
        Assert.Equal(0, calls);
        Assert.Equal(0, service.OutstandingCount);
    }

    [Fact]
    public async Task TryEnqueue_WhenWelcomeDisabled_DoesNotQueueWork()
    {
        var calls = 0;
        var delivery = new StubDelivery((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        await using var service = CreateService(
            delivery,
            welcomeOptions: new NewMemberWelcomeOptions(false, string.Empty));
        service.Start();

        var accepted = service.TryEnqueue(42, isBot: false);
        await Task.Delay(25);

        Assert.False(accepted);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TryEnqueue_DuplicatePendingUserIsCoalesced()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var delivery = new StubDelivery(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        await using var service = CreateService(delivery);
        service.Start();

        Assert.True(service.TryEnqueue(7, isBot: false));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.TryEnqueue(7, isBot: false));
        Assert.Equal(1, service.OutstandingCount);

        release.TrySetResult();
        await WaitUntilAsync(() => service.OutstandingCount == 0);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TryEnqueue_NeverExceedsMaximumOutstandingWork()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new StubDelivery(async (userId, cancellationToken) =>
        {
            if (userId == 1)
            {
                firstStarted.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
        });
        var runtimeOptions = TestRuntimeOptions(maximumOutstanding: 2, workerCount: 1);
        await using var service = CreateService(delivery, runtimeOptions: runtimeOptions);
        service.Start();

        Assert.True(service.TryEnqueue(1, isBot: false));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(service.TryEnqueue(2, isBot: false));
        Assert.False(service.TryEnqueue(3, isBot: false));
        Assert.Equal(2, service.OutstandingCount);

        release.TrySetResult();
        await WaitUntilAsync(() => service.OutstandingCount == 0);
    }

    [Fact]
    public async Task StopAsync_StopsAdmissionAndCancelsStalledActiveDeliveryAfterDrainBound()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new StubDelivery(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled.TrySetResult();
                throw;
            }
        });
        var runtimeOptions = TestRuntimeOptions(
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(25),
            shutdownCancellationGrace: TimeSpan.FromSeconds(1));
        await using var service = CreateService(delivery, runtimeOptions: runtimeOptions);
        service.Start();
        Assert.True(service.TryEnqueue(1, isBot: false));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(service.TryEnqueue(2, isBot: false));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, service.OutstandingCount);
    }

    [Fact]
    public async Task StartAndStop_AreIdempotent()
    {
        var calls = 0;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new StubDelivery((_, _) =>
        {
            Interlocked.Increment(ref calls);
            delivered.TrySetResult();
            return Task.CompletedTask;
        });
        await using var service = CreateService(delivery);

        service.Start();
        service.Start();
        Assert.True(service.TryEnqueue(1, isBot: false));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync();
        await service.StopAsync();

        Assert.Equal(1, calls);
    }

    private static NewMemberWelcomeService CreateService(
        INewMemberWelcomeDelivery delivery,
        NewMemberWelcomeOptions? welcomeOptions = null,
        NewMemberWelcomeRuntimeOptions? runtimeOptions = null)
        => new(
            delivery,
            welcomeOptions ?? new NewMemberWelcomeOptions(true, "welcome"),
            runtimeOptions ?? TestRuntimeOptions(),
            NullLogger<NewMemberWelcomeService>.Instance);

    private static NewMemberWelcomeRuntimeOptions TestRuntimeOptions(
        int maximumOutstanding = 8,
        int workerCount = 2,
        TimeSpan? shutdownDrainTimeout = null,
        TimeSpan? shutdownCancellationGrace = null)
        => new(
            MaximumOutstanding: maximumOutstanding,
            WorkerCount: workerCount,
            MaximumDiscordOperations: 4,
            OperationTimeout: TimeSpan.FromSeconds(1),
            ShutdownDrainTimeout: shutdownDrainTimeout ?? TimeSpan.FromSeconds(1),
            ShutdownCancellationGrace: shutdownCancellationGrace ?? TimeSpan.FromSeconds(1));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met before the test deadline.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class StubDelivery(Func<ulong, CancellationToken, Task> deliver)
        : INewMemberWelcomeDelivery
    {
        private readonly Func<ulong, CancellationToken, Task> _deliver = deliver;

        public bool HasActiveOperation { get; set; }

        public Task DeliverAsync(ulong userId, CancellationToken cancellationToken)
            => _deliver(userId, cancellationToken);
    }
}
