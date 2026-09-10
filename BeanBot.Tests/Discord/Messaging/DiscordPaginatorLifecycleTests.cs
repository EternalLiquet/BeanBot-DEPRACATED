using System.Collections.Concurrent;
using System.Reflection;
using BeanBot.Discord.Messaging;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.Messaging;

public class DiscordPaginatorLifecycleTests
{
    [Fact]
    public async Task SendAsync_CreatesPageAndAllControlsThenStopDeletesOnce()
    {
        using var fixture = new PaginatorFixture();

        Assert.Same(fixture.Message, await fixture.SendAsync());

        Assert.Equal("first", Assert.Single(fixture.Embeds).Description);
        Assert.Equal("Page 1/2", fixture.Embeds[0].Footer?.Text);
        Assert.Equal(5, fixture.AddedControls.Distinct().Count());
        Assert.Single(fixture.Sessions);
        Assert.Equal(DiscordPaginatorService.MaximumActivePaginators - 1, fixture.Slots.CurrentCount);
        await fixture.StopAsync();
        await fixture.StopAsync();
        Assert.Equal(1, fixture.DeleteCount);
        fixture.AssertSessionReleased();
    }

    [Fact]
    public async Task SendAsync_StalledInitialSendReleasesSessionSlotButRetainsRawOperation()
    {
        using var fixture = new PaginatorFixture();
        var stalled = new TaskCompletionSource<IUserMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SendMessage = () => stalled.Task;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.SendAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, fixture.SendCount);
            Assert.Empty(fixture.AddedControls);
            fixture.AssertSessionReleased();
            Assert.Equal(1, fixture.Operations.OwnedOperationCount);
        }
        finally
        {
            stalled.TrySetResult(fixture.Message);
            await fixture.Operations.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(0, fixture.Operations.OwnedOperationCount);
        fixture.AssertSessionReleased();
    }

    [Fact]
    public async Task SendAsync_StalledControlSetupStopsSessionWithoutRetrying()
    {
        using var fixture = new PaginatorFixture();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AddControl = () => stalled.Task;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.SendAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, fixture.SendCount);
            Assert.Single(fixture.AddedControls);
            fixture.AssertSessionReleased();
            Assert.Equal(1, fixture.Operations.OwnedOperationCount);
            Assert.Equal(0, fixture.DeleteCount);
        }
        finally
        {
            stalled.TrySetException(new InvalidOperationException("late control failure"));
            await fixture.Operations.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(0, fixture.Operations.OwnedOperationCount);
        fixture.AssertSessionReleased();
    }

    [Fact]
    public async Task Expiration_StalledControlRemovalRemainsBoundedAndRetainsEachRawOperation()
    {
        using var fixture = new PaginatorFixture();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RemoveControl = () => stalled.Task;
        try
        {
            await fixture.SendAsync(TimeSpan.FromMilliseconds(100));
            var session = Assert.Single(fixture.Sessions).Value;
            await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(5, fixture.RemovedControls.Count);
            Assert.Equal(5, fixture.RemovedControls.Distinct().Count());
            Assert.All(fixture.RemovedUserIds, id => Assert.Equal(99UL, id));
            Assert.Equal(5, fixture.Operations.OwnedOperationCount);
            fixture.AssertSessionReleased();
            await fixture.StopAsync();
            Assert.Equal(0, fixture.DeleteCount);
        }
        finally
        {
            stalled.TrySetResult();
            await fixture.Operations.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(0, fixture.Operations.OwnedOperationCount);
        fixture.AssertSessionReleased();
    }

    [Fact]
    public async Task Stop_StalledDeleteCompletesSessionOnceAndRetainsRawOperation()
    {
        using var fixture = new PaginatorFixture();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.DeleteMessage = () => stalled.Task;
        try
        {
            await fixture.SendAsync();
            var session = Assert.Single(fixture.Sessions).Value;
            await Task.WhenAll(fixture.StopAsync(), fixture.StopAsync()).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(session.CompletionTask.IsCompletedSuccessfully);
            Assert.Equal(1, fixture.DeleteCount);
            Assert.Equal(1, fixture.Operations.OwnedOperationCount);
            fixture.AssertSessionReleased();
        }
        finally
        {
            stalled.TrySetResult();
            await fixture.Operations.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        fixture.AssertSessionReleased();
    }

    private sealed class PaginatorFixture : IDisposable
    {
        private readonly DiscordSocketClient _client = new();
        private readonly ICommandContext _context;
        private readonly DiscordPaginatorService _paginator;
        public IUserMessage Message { get; }
        public Func<Task<IUserMessage>> SendMessage { get; set; }
        public Func<Task> AddControl { get; set; } = () => Task.CompletedTask;
        public Func<Task> RemoveControl { get; set; } = () => Task.CompletedTask;
        public Func<Task> DeleteMessage { get; set; } = () => Task.CompletedTask;
        public List<Embed> Embeds { get; } = [];
        public List<string> AddedControls { get; } = [];
        public List<string> RemovedControls { get; } = [];
        public List<ulong> RemovedUserIds { get; } = [];
        public int SendCount { get; private set; }
        public int DeleteCount { get; private set; }
        public ConcurrentDictionary<ulong, DiscordPaginatorService.PaginationSession> Sessions =>
            GetField<ConcurrentDictionary<ulong, DiscordPaginatorService.PaginationSession>>("_sessions");
        public SemaphoreSlim Slots => GetField<SemaphoreSlim>("_availableSlots");
        public PaginatorDiscordOperationTracker Operations => GetField<PaginatorDiscordOperationTracker>("_discordOperations");

        public PaginatorFixture()
        {
            _paginator = new DiscordPaginatorService(_client, NullLogger<DiscordPaginatorService>.Instance,
                TimeSpan.FromMilliseconds(25), () => 99);
            Message = CreateProxy<IUserMessage>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_Id": return 7UL;
                    case nameof(IMessage.AddReactionAsync):
                        AddedControls.Add(args[0]!.ToString()!);
                        return AddControl();
                    case nameof(IMessage.RemoveReactionAsync):
                        RemovedControls.Add(args[0]!.ToString()!);
                        RemovedUserIds.Add(Assert.IsType<ulong>(args[1]));
                        return RemoveControl();
                    case nameof(IMessage.DeleteAsync):
                        DeleteCount++;
                        return DeleteMessage();
                    default: throw new InvalidOperationException(method.Name);
                }
            });
            SendMessage = () => Task.FromResult(Message);
            var channel = CreateProxy<IMessageChannel>((method, args) =>
            {
                Assert.Equal(nameof(IMessageChannel.SendMessageAsync), method.Name);
                SendCount++;
                Embeds.Add(Assert.Single(args.OfType<Embed>()));
                return SendMessage();
            });
            var user = CreateProxy<IUser>((method, _) => method.Name == "get_Id" ? 42UL : throw new InvalidOperationException(method.Name));
            _context = CreateProxy<ICommandContext>((method, _) => method.Name switch
            {
                "get_Channel" => channel,
                "get_User" => user,
                _ => throw new InvalidOperationException(method.Name)
            });
        }

        public Task<IUserMessage> SendAsync(TimeSpan? timeout = null) => _paginator.SendAsync(_context, ["first", "second"], timeout);
        public Task StopAsync() => _paginator.HandleReactionAsync(7, 42, new Emoji("\u23f9"));
        public void AssertSessionReleased()
        {
            Assert.Empty(Sessions);
            Assert.Equal(DiscordPaginatorService.MaximumActivePaginators, Slots.CurrentCount);
        }
        private T GetField<T>(string name) => Assert.IsType<T>(typeof(DiscordPaginatorService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_paginator));
        public void Dispose()
        {
            _paginator.Dispose();
            _client.Dispose();
        }
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, DiscordProxy>();
        ((DiscordProxy)(object)proxy).InvokeMethod = invoke;
        return proxy;
    }

    public class DiscordProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?>? InvokeMethod { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod!(targetMethod!, args ?? []);
    }
}
