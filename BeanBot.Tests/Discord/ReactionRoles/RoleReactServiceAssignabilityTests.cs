using System.Reflection;
using BeanBot.Discord.ReactionRoles;
using Discord;
using Xunit;

namespace BeanBot.Tests.Discord.ReactionRoles;

public class RoleReactServiceAssignabilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyRoleChangeAsync_EveryoneTargetNeverLooksUpMember(bool addRole)
    {
        var guild = DispatchProxy.Create<IGuild, DiscordCallRecorder>();
        var role = DispatchProxy.Create<IRole, DiscordCallRecorder>();
        var status = await RoleReactService.ApplyRoleChangeAsync(
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

        var status = await RoleReactService.ApplyRoleChangeAsync(
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

        var status = await RoleReactService.ApplyRoleChangeAsync(
            facts, guild, role, 42, addRole, CancellationToken.None);

        Assert.Equal(ReactionRoleAssignabilityStatus.Allowed, status);
        Assert.Equal(new[] { "GetUserAsync" }, ((DiscordCallRecorder)guild).Calls);
        Assert.Equal(new[] { addRole ? "AddRoleAsync" : "RemoveRoleAsync" }, userRecorder.Calls);
    }

    public class DiscordCallRecorder : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public IGuildUser? User { get; set; }
        public IReadOnlyCollection<ulong> RoleIds { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name ?? throw new InvalidOperationException();
            if (name == "get_Id") return 7UL;
            if (name == "get_RoleIds") return RoleIds;
            Calls.Add(name);
            return name switch
            {
                "GetUserAsync" => Task.FromResult(User!),
                "AddRoleAsync" or "RemoveRoleAsync" => Task.CompletedTask,
                _ => throw new NotSupportedException(name)
            };
        }
    }
}
