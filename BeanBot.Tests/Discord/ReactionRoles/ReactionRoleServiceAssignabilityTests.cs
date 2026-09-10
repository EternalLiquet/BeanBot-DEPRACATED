using System.Reflection;
using BeanBot.Discord.ReactionRoles;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class ReactionRoleServiceAssignabilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CoordinatedLateMutation_ConvergesDespiteStaleCachedMembership(bool firstAddsRole)
    {
        var guild = DispatchProxy.Create<IGuild, DiscordCallRecorder>();
        var user = DispatchProxy.Create<IGuildUser, DiscordCallRecorder>();
        var role = DispatchProxy.Create<IRole, DiscordCallRecorder>();
        ((DiscordCallRecorder)guild).User = user;
        var userRecorder = (DiscordCallRecorder)user;
        userRecorder.RoleIds = firstAddsRole ? [] : [7];
        var rawMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        userRecorder.Mutation = rawMutation.Task;
        var coordinator = new ReactionRoleMutationCoordinator(1, NullLogger.Instance, TimeSpan.FromMilliseconds(25));
        var key = new ReactionRoleMutationKey(1, 42, 7);
        async Task Mutate(bool desired, CancellationToken token)
            => await ReactionRoleService.ApplyRoleChangeAsync(
                new ReactionRoleAssignabilityFacts(true, false, false, true, 1, 2), guild, role, 42, desired, token);
        var owner = coordinator.Submit(key, 8, firstAddsRole, Mutate, CancellationToken.None)!;
        await userRecorder.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => owner);
        Assert.Null(coordinator.Submit(key, 8, !firstAddsRole, Mutate, CancellationToken.None));
        var workers = coordinator.SnapshotOperations();

        rawMutation.SetResult();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(firstAddsRole
            ? new[] { "AddRoleAsync", "RemoveRoleAsync" }
            : ["RemoveRoleAsync", "AddRoleAsync"], userRecorder.Calls);
        Assert.Equal(0, coordinator.ActiveKeyCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyRoleChangeAsync_LateMemberLookupAfterCancellationNeverMutates(bool addRole)
    {
        using var cancellation = new CancellationTokenSource();
        var guild = DispatchProxy.Create<IGuild, DiscordCallRecorder>();
        var user = DispatchProxy.Create<IGuildUser, DiscordCallRecorder>();
        var role = DispatchProxy.Create<IRole, DiscordCallRecorder>();
        var memberLookup = new TaskCompletionSource<IGuildUser>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guildRecorder = (DiscordCallRecorder)guild;
        guildRecorder.Lookup = memberLookup.Task;
        var operation = ReactionRoleService.ApplyRoleChangeAsync(
            new ReactionRoleAssignabilityFacts(true, false, false, true, 1, 2),
            guild, role, 42, addRole, cancellation.Token);
        Assert.Equal(new[] { "GetUserAsync" }, guildRecorder.Calls);
        cancellation.Cancel();
        memberLookup.SetResult(user);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Empty(((DiscordCallRecorder)user).Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyRoleChangeAsync_EveryoneTargetNeverLooksUpMember(bool addRole)
    {
        var guild = DispatchProxy.Create<IGuild, DiscordCallRecorder>();
        var role = DispatchProxy.Create<IRole, DiscordCallRecorder>();
        var status = await ReactionRoleService.ApplyRoleChangeAsync(
            new ReactionRoleAssignabilityFacts(true, true, false, true, 1, 2),
            guild, role, 42, addRole, CancellationToken.None);

        Assert.Equal(ReactionRoleAssignabilityStatus.EveryoneRole, status);
        Assert.Empty(((DiscordCallRecorder)guild).Calls);
    }

    [Theory]
    [InlineData(false, false, true, 1, 2, true)]
    [InlineData(false, false, true, 1, 2, false)]
    [InlineData(true, true, true, 1, 2, true)]
    [InlineData(true, true, true, 1, 2, false)]
    [InlineData(true, false, false, 1, 2, true)]
    [InlineData(true, false, false, 1, 2, false)]
    [InlineData(true, false, true, 2, 2, true)]
    [InlineData(true, false, true, 2, 2, false)]
    [InlineData(true, false, true, 3, 2, true)]
    [InlineData(true, false, true, 3, 2, false)]
    public async Task ApplyRoleChangeAsync_StaleTargetNeverLooksUpMemberOrMutatesRoles(
        bool exists, bool managed, bool botCanManage, int rolePosition, int botHierarchy, bool addRole)
    {
        var guild = DispatchProxy.Create<IGuild, DiscordCallRecorder>();
        var role = exists ? DispatchProxy.Create<IRole, DiscordCallRecorder>() : null;
        var recorder = (DiscordCallRecorder)guild;
        var facts = new ReactionRoleAssignabilityFacts(exists, false, managed, botCanManage, rolePosition, botHierarchy);

        var status = await ReactionRoleService.ApplyRoleChangeAsync(
            facts, guild, role, 42, addRole, CancellationToken.None);

        Assert.NotEqual(ReactionRoleAssignabilityStatus.Allowed, status);
        Assert.Empty(recorder.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyRoleChangeAsync_AllowedTargetPerformsOnlyRequestedMutation(bool addRole)
    {
        var guild = DispatchProxy.Create<IGuild, DiscordCallRecorder>();
        var user = DispatchProxy.Create<IGuildUser, DiscordCallRecorder>();
        var role = DispatchProxy.Create<IRole, DiscordCallRecorder>();
        ((DiscordCallRecorder)guild).User = user;
        var userRecorder = (DiscordCallRecorder)user;
        userRecorder.RoleIds = addRole ? [] : [7];
        var facts = new ReactionRoleAssignabilityFacts(true, false, false, true, 1, 2);

        var status = await ReactionRoleService.ApplyRoleChangeAsync(
            facts, guild, role, 42, addRole, CancellationToken.None);

        Assert.Equal(ReactionRoleAssignabilityStatus.Allowed, status);
        Assert.Equal(new[] { "GetUserAsync" }, ((DiscordCallRecorder)guild).Calls);
        Assert.Equal(new[] { addRole ? "AddRoleAsync" : "RemoveRoleAsync" }, userRecorder.Calls);
    }

    public class DiscordCallRecorder : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public IGuildUser? User { get; set; }
        public Task<IGuildUser>? Lookup { get; set; }
        public Task Mutation { get; set; } = Task.CompletedTask;
        public TaskCompletionSource MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<ulong> RoleIds { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name ?? throw new InvalidOperationException();
            if (name == "get_Id") return 7UL;
            if (name == "get_RoleIds") return RoleIds;
            Calls.Add(name);
            if (name is "AddRoleAsync" or "RemoveRoleAsync") MutationStarted.TrySetResult();
            return name switch
            {
                "GetUserAsync" => Lookup ?? Task.FromResult(User!),
                "AddRoleAsync" or "RemoveRoleAsync" => Mutation,
                _ => throw new NotSupportedException(name)
            };
        }
    }
}
