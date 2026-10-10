using System.Reflection;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.RoleMenus;
using BeanBot.Hosting;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class InteractionShutdownTests
{
    [Fact]
    public async Task TimedOutRepairFeedback_KeepsHandlerAndRuntimeOwnedUntilLateMutationSettles()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BeanBot:BotToken"] = "test-token",
            ["BeanBot:MongoConnectionString"] = "mongodb://127.0.0.1:27017",
            ["BeanBot:GeneralChannelId"] = "1",
            ["BeanBot:HatoeteUrl"] = "https://example.com/a.png",
            ["BeanBot:YoshimaruUrl"] = "https://example.com/b.png"
        });
        builder.Services.AddBeanBot(builder.Configuration);
        builder.Services.AddBeanBotInteractions();
        builder.Services.AddSingleton(provider => new InteractionHandler(
            provider.GetRequiredService<DiscordSocketClient>(),
            provider.GetRequiredService<InteractionService>(),
            provider,
            provider.GetRequiredService<InteractionExecutionContext>(),
            provider.GetRequiredService<InteractionCommandRegistrationTarget>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            NullLogger<InteractionHandler>.Instance,
            TimeSpan.FromMilliseconds(25)));
        using var host = builder.Build();
        using var client = host.Services.GetRequiredService<DiscordSocketClient>();
        var handler = host.Services.GetRequiredService<InteractionHandler>();
        var runtime = host.Services.GetRequiredService<IBeanBotRuntime>();
        var roleMenus = host.Services.GetRequiredService<RoleMenuInteractionService>();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedbackSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCancellation = new CancellationTokenSource();

        Assert.Equal(InteractionOperationAdmission.Started, handler.StartOperation(async _ =>
        {
            var mutation = roleMenus.RunMenuMutationAsync(
                ObjectId.GenerateNewId(),
                async _ =>
                {
                    sendStarted.SetResult();
                    await finishSend.Task;
                    writeStarted.SetResult();
                    await finishWrite.Task;
                    return "repaired";
                },
                callerCancellation.Token);
            await sendStarted.Task;
            callerCancellation.CancelAfter(TimeSpan.FromMilliseconds(25));
            await RoleMenuRepairWorkflow.WaitForOwnedMutationAsync(
                mutation,
                () =>
                {
                    feedbackSent.SetResult();
                    return Task.CompletedTask;
                },
                callerCancellation.Token);
        }));

        try
        {
            await feedbackSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(handler.HasPendingOperations);
            Assert.True(runtime.HasActiveDiscordLifecycleOperation);
            await handler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(handler.HasPendingOperations);
            Assert.True(runtime.HasActiveDiscordLifecycleOperation);
            var calls = new List<string>();
            var runtimeProxy = DispatchProxy.Create<IBeanBotRuntime, RuntimeProxy>();
            ((RuntimeProxy)runtimeProxy).Handler = method =>
            {
                calls.Add(method.Name);
                if (method.Name == "get_HasActiveDiscordLifecycleOperation")
                    return runtime.HasActiveDiscordLifecycleOperation;
                if (method.Name == "get_CanDisposeDiscordClient") return true;
                if (method.ReturnType == typeof(Task<bool>)) return Task.FromResult(true);
                if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
                return null;
            };
            var application = new BeanBotApplication(
                runtimeProxy, NullLogger<BeanBotApplication>.Instance);
            await application.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.DoesNotContain(nameof(IBeanBotRuntime.ReleaseInstanceLeaseAsync), calls);
            Assert.DoesNotContain(nameof(IBeanBotRuntime.DisposeDiscordClient), calls);

            finishSend.SetResult();
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(handler.HasPendingOperations);
            Assert.True(runtime.HasActiveDiscordLifecycleOperation);
        }
        finally
        {
            finishSend.TrySetResult();
            finishWrite.TrySetResult();
        }

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (handler.HasPendingOperations && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.False(handler.HasPendingOperations);
        Assert.False(runtime.HasActiveDiscordLifecycleOperation);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("busy response")]
    [InlineData("registration")]
    public async Task StalledDiscordWork_SurvivesDrainAndPreventsApplicationTeardown(string operationKind)
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationCalls = 0;
        var registration = new InteractionCommandRegistration(() =>
        {
            registrationCalls++;
            return stalled.Task;
        }, TimeSpan.FromMilliseconds(25));
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BeanBot:BotToken"] = "test-token",
            ["BeanBot:MongoConnectionString"] = "mongodb://127.0.0.1:27017",
            ["BeanBot:GeneralChannelId"] = "1",
            ["BeanBot:HatoeteUrl"] = "https://example.com/a.png",
            ["BeanBot:YoshimaruUrl"] = "https://example.com/b.png"
        });
        builder.Services.AddBeanBot(builder.Configuration);
        builder.Services.AddBeanBotInteractions();
        builder.Services.AddSingleton(provider => new InteractionHandler(
            provider.GetRequiredService<DiscordSocketClient>(),
            provider.GetRequiredService<InteractionService>(),
            provider,
            provider.GetRequiredService<InteractionExecutionContext>(),
            provider.GetRequiredService<InteractionCommandRegistrationTarget>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            NullLogger<InteractionHandler>.Instance,
            TimeSpan.FromMilliseconds(25),
            registration));
        var host = builder.Build();
        using var client = host.Services.GetRequiredService<DiscordSocketClient>();
        var handler = host.Services.GetRequiredService<InteractionHandler>();
        var realRuntime = host.Services.GetRequiredService<IBeanBotRuntime>();
        CancellationToken operationToken = default;
        Task BeginOperation(CancellationToken token)
        {
            operationToken = token;
            return stalled.Task;
        }

        var calls = new List<string>();
        var runtime = DispatchProxy.Create<IBeanBotRuntime, RuntimeProxy>();
        ((RuntimeProxy)runtime).Handler = method =>
        {
            calls.Add(method.Name);
            if (method.Name == "get_HasActiveDiscordLifecycleOperation") return realRuntime.HasActiveDiscordLifecycleOperation;
            if (method.Name == "get_CanDisposeDiscordClient") return true;
            if (method.ReturnType == typeof(Task<bool>)) return Task.FromResult(true);
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            return null;
        };
        try
        {
            if (operationKind == "registration")
            {
                await handler.HandleReadyAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            else
            {
                var admission = operationKind == "command"
                    ? handler.StartOperation(BeginOperation)
                    : handler.StartBusyResponse(BeginOperation);
                Assert.Equal(InteractionOperationAdmission.Started, admission);
            }

            await handler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(handler.HasPendingOperations);
            Assert.True(realRuntime.HasActiveDiscordLifecycleOperation);
            if (operationKind != "registration") Assert.True(operationToken.IsCancellationRequested);

            var application = new BeanBotApplication(runtime, NullLogger<BeanBotApplication>.Instance);
            await application.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.DoesNotContain(nameof(IBeanBotRuntime.StopDiscordAsync), calls);
            Assert.DoesNotContain(nameof(IBeanBotRuntime.DisposeDiscordClient), calls);
            Assert.Equal(InteractionOperationAdmission.Stopping, handler.StartOperation(BeginOperation));
            Assert.Equal(InteractionOperationAdmission.Stopping, handler.StartBusyResponse(BeginOperation));
            await handler.HandleReadyAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(operationKind == "registration" ? 1 : 0, registrationCalls);
        }
        finally
        {
            stalled.TrySetException(new InvalidOperationException("late interaction failure"));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (handler.HasPendingOperations && DateTime.UtcNow < deadline) await Task.Delay(5);
            await ((IAsyncDisposable)host).DisposeAsync();
        }

        Assert.False(handler.HasPendingOperations);
        Assert.False(realRuntime.HasActiveDiscordLifecycleOperation);
        Assert.Equal(operationKind == "registration" ? 1 : 0, registrationCalls);
    }

    [Fact]
    public async Task Registration_ApplicationStoppingRejectsReadyBeforeAnyRequest()
    {
        using var stopping = new CancellationTokenSource();
        var calls = 0;
        var registration = new InteractionCommandRegistration(() =>
        {
            calls++;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), stopping.Token);
        stopping.Cancel();

        Assert.False(await registration.EnsureRegisteredAsync());
        Assert.False(registration.HasPendingOperations);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Registration_StopBeforeReadyPermanentlyClosesAdmission()
    {
        var calls = 0;
        var registration = new InteractionCommandRegistration(() =>
        {
            calls++;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1));

        await registration.StopAsync(TimeSpan.FromMilliseconds(25));
        await registration.StopAsync(TimeSpan.FromMilliseconds(25));
        Assert.False(await registration.EnsureRegisteredAsync());
        Assert.False(registration.HasPendingOperations);
        Assert.Equal(0, calls);
    }

    public class RuntimeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!);
    }
}
