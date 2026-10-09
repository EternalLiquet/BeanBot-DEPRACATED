using System.Reflection;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;
using BeanBot.Discord.Messaging;
using BeanBot.Hosting;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BeanBot.Tests.Discord.Events;

public class LegacyCommandExecutionCoordinatorTests
{
    [Fact]
    public async Task TryStart_NormalExecution_RunsOnceAndReleasesCapacity()
    {
        var coordinator = new LegacyCommandExecutionCoordinator();
        var calls = 0;
        var result = coordinator.TryStart(() => { calls++; return Task.CompletedTask; }, _ => { }, out var completion);
        await completion;
        Assert.True((await coordinator.DrainAsync()).IsDrained);
        Assert.Equal(LegacyCommandAdmissionResult.Admitted, result);
        Assert.Equal(1, calls);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task TryStart_AtCapacity_RejectsWithoutQueueing()
    {
        var coordinator = new LegacyCommandExecutionCoordinator(maximumConcurrentExecutions: 2);
        var release = NewCompletion();
        Assert.Equal(LegacyCommandAdmissionResult.Admitted, coordinator.TryStart(() => release.Task, _ => { }, out var first));
        Assert.Equal(LegacyCommandAdmissionResult.Admitted, coordinator.TryStart(() => release.Task, _ => { }, out var second));
        var rejectedCalls = 0;
        var rejected = coordinator.TryStart(() => { rejectedCalls++; return Task.CompletedTask; }, _ => { }, out _);
        Assert.Equal(LegacyCommandAdmissionResult.RejectedCapacity, rejected);
        Assert.Equal(2, coordinator.ActiveExecutionCount);
        Assert.Equal(0, rejectedCalls);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.True((await coordinator.DrainAsync()).IsDrained);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryStart_FailureIsObservedOnceEvenWhenNoCallerAwaitsIt(bool throwSynchronously)
    {
        var coordinator = new LegacyCommandExecutionCoordinator(maximumConcurrentExecutions: 1);
        var expected = new InvalidOperationException("injected failure");
        var observed = NewCompletion();
        var reports = 0;
        coordinator.TryStart(
            () => throwSynchronously ? throw expected : Task.FromException(expected),
            exception => { Assert.Same(expected, exception); Interlocked.Increment(ref reports); observed.SetResult(); },
            out var completion);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => completion));
        Assert.True((await coordinator.DrainAsync()).IsDrained);
        Assert.Equal(1, reports);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task TryStart_CancellationRemainsCancellationAndReleasesCapacity()
    {
        var coordinator = new LegacyCommandExecutionCoordinator();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reports = 0;
        coordinator.TryStart(() => Task.FromCanceled(cancellation.Token), _ => reports++, out var completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.True((await coordinator.DrainAsync()).IsDrained);
        Assert.True(completion.IsCanceled);
        Assert.Equal(0, reports);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task DrainAsync_TimeoutRetainsSurvivorUntilLateFailureActuallyCompletes()
    {
        var coordinator = new LegacyCommandExecutionCoordinator(drainTimeout: TimeSpan.FromMilliseconds(20));
        var release = NewCompletion();
        var reports = 0;
        coordinator.TryStart(() => release.Task, _ => Interlocked.Increment(ref reports), out var completion);
        var result = await coordinator.DrainAsync();
        Assert.False(result.IsDrained);
        Assert.Equal(1, result.SurvivingExecutionCount);
        Assert.Equal(1, coordinator.ActiveExecutionCount);
        Assert.False(coordinator.WhenDrained.IsCompleted);
        Assert.Equal(LegacyCommandAdmissionResult.RejectedStopping, coordinator.TryStart(() => Task.CompletedTask, _ => { }, out _));
        release.SetException(new InvalidOperationException("late failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
        await coordinator.WhenDrained.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, reports);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task StopAdmission_IsIdempotentAndPreventsFutureExecution()
    {
        var coordinator = new LegacyCommandExecutionCoordinator();
        coordinator.StopAdmission();
        coordinator.StopAdmission();
        Assert.Equal(LegacyCommandAdmissionResult.RejectedStopping, coordinator.TryStart(() => Task.CompletedTask, _ => { }, out _));
        Assert.True((await coordinator.DrainAsync()).IsDrained);
        Assert.True((await coordinator.DrainAsync()).IsDrained);
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task RealCommandPipeline_ReturnsAfterAdmissionAndOwnsBodyAndCompletionFeedback()
    {
        using var waiter = new BoundedMessageWaiter<string>(1);
        var state = new BlockingCommandState(waiter);
        using var services = new ServiceCollection().AddSingleton(state).BuildServiceProvider();
        using var commands = new CommandService(new CommandServiceConfig { DefaultRunMode = RunMode.Sync });
        await commands.AddModuleAsync<BlockingModule>(services);
        Assert.Contains(commands.Commands, command => command.Aliases.Contains("blocking"));
        var context = DispatchProxy.Create<ICommandContext, CommandContextProxy>();
        var feedbackStarted = NewCompletion();
        var releaseFeedback = NewCompletion();
        IResult? commandResult = null;
        commands.CommandExecuted += async (_, _, result) => { commandResult = result; feedbackStarted.TrySetResult(); await releaseFeedback.Task; };
        var executionFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new LegacyCommandExecutionCoordinator(maximumConcurrentExecutions: 1, drainTimeout: TimeSpan.FromMilliseconds(20));

        Task<IResult>? executeResult = null;
        var admission = coordinator.TryStart(() => executeResult = commands.ExecuteAsync(context, "blocking", services), exception => executionFailure.TrySetResult(exception), out var completion);
        Assert.Equal(LegacyCommandAdmissionResult.Admitted, admission);
        await Task.WhenAny(state.Started.Task, feedbackStarted.Task, executionFailure.Task, completion).WaitAsync(TimeSpan.FromSeconds(1));
        if (completion.IsCompleted)
        {
            await completion;
            var result = await executeResult!;
            Assert.True(result.IsSuccess, result.ErrorReason);
        }
        if (executionFailure.Task.IsCompleted) throw new Xunit.Sdk.XunitException((await executionFailure.Task).ToString());
        Assert.True(commandResult is null || commandResult.IsSuccess, commandResult?.ErrorReason);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(completion.IsCompleted);
        Assert.Equal(1, coordinator.ActiveExecutionCount);
        Assert.Equal(LegacyCommandAdmissionResult.RejectedCapacity,
            coordinator.TryStart(() => commands.ExecuteAsync(context, "blocking", services), _ => { }, out _));

        Assert.True(waiter.TryPublish(10, 20, false, "answer"));
        await feedbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("answer", state.Answer);
        Assert.Equal(1, coordinator.ActiveExecutionCount);
        Assert.False((await coordinator.DrainAsync()).IsDrained);
        releaseFeedback.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.WhenDrained.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task ProductionCommands_AllRunSynchronouslyInsideBeanBotOwnership()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BeanBot:BotToken"] = "test-token",
            ["BeanBot:MongoConnectionString"] = "mongodb://127.0.0.1:27017",
            ["BeanBot:GeneralChannelId"] = "1",
            ["BeanBot:HatoeteUrl"] = "https://example.com/a.png",
            ["BeanBot:YoshimaruUrl"] = "https://example.com/b.png"
        });
        builder.Services.AddBeanBot(builder.Configuration);
        using var host = builder.Build();
        using var client = host.Services.GetRequiredService<DiscordSocketClient>();
        var commands = host.Services.GetRequiredService<CommandService>();
        await commands.AddModulesAsync(typeof(AdministrativeModule).Assembly, host.Services);
        Assert.NotEmpty(commands.Commands);
        Assert.All(commands.Commands, command => Assert.Equal(RunMode.Sync, command.RunMode));
        Assert.Contains(commands.Commands, command => command.Aliases.Contains("role setting"));
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class BlockingCommandState
    {
        private readonly BoundedMessageWaiter<string> _waiter;
        internal BlockingCommandState(BoundedMessageWaiter<string> waiter) => _waiter = waiter;
        public TaskCompletionSource Started { get; } = NewCompletion();
        public string? Answer { get; private set; }
        public async Task RunAsync()
        {
            var answer = _waiter.WaitAsync(10, 20, TimeSpan.FromSeconds(2));
            Started.SetResult();
            Answer = await answer;
        }
    }

    public class BlockingModule(BlockingCommandState state) : ModuleBase<ICommandContext>
    {
        [Command("blocking", RunMode = RunMode.Sync)]
        public Task BlockingAsync() => state.RunAsync();
    }

    public class CommandContextProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_Guild" => null,
                "get_User" => DispatchProxy.Create<IUser, CommandContextProxy>(),
                "get_Channel" => DispatchProxy.Create<IMessageChannel, CommandContextProxy>(),
                "get_Message" => DispatchProxy.Create<IUserMessage, CommandContextProxy>(),
                "get_Client" => DispatchProxy.Create<IDiscordClient, CommandContextProxy>(),
                "get_CurrentUser" => DispatchProxy.Create<ISelfUser, CommandContextProxy>(),
                "get_Id" => 1UL,
                "get_Username" => "test-user",
                "get_Content" => "blocking",
                "get_IsBot" => false,
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }
}
