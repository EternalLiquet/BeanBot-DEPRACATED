using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuServiceIntegrationTests
{
    [Fact]
    public async Task ConcurrentMembers_KeepMutationTargetsLocalToEachRequest()
    {
        var fixture = new Fixture();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeMutation = async memberId =>
        {
            if (memberId == 3UL)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        };

        var first = fixture.Members.ApplySelectionAsync(
            fixture.Settings.Id, 5UL, ["10", "11"], 1UL, 4UL, 2UL, 3UL, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var second = await fixture.Members.ApplySelectionAsync(
                fixture.Settings.Id, 5UL, ["11"], 1UL, 4UL, 2UL, 6UL, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("11", second, StringComparison.Ordinal);
            Assert.Equal([11UL, 99UL], fixture.MemberRoles[6UL].Order());
            Assert.Equal([99UL], fixture.MemberRoles[3UL]);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([10UL, 11UL, 99UL], fixture.MemberRoles[3UL].Order());
        Assert.Equal([11UL, 99UL], fixture.MemberRoles[6UL].Order());
        Assert.Equal([(6UL, 11UL), (3UL, 10UL), (3UL, 11UL)], fixture.Mutations);
    }

    [Fact]
    public async Task ForgedPanel_IsRejectedBeforeMembershipReadsOrMutations()
    {
        var fixture = new Fixture();
        var selector = await fixture.Members.LoadSelectorAsync(
            fixture.Settings.Id, 1UL, 4UL, 5UL, 999UL, 2UL, 3UL, true, CancellationToken.None);

        Assert.Null(selector.Components);
        Assert.Contains("invalid", selector.Content, StringComparison.Ordinal);
        Assert.Equal(0, fixture.UserReads);
        Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task CreatePreview_RefreshesPermissionAndRejectsEveryoneBeforeChannelRead()
    {
        var fixture = new Fixture();
        var preview = await fixture.Administration.CreatePreviewAsync(
            new RoleMenuCreateRequest("Roles", null, "multiple", 4UL, 1UL, ChannelType.Text, [1UL]),
            1UL, 3UL, 2UL, CancellationToken.None);

        Assert.Null(preview.Draft);
        Assert.Contains("everyone", preview.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.UserReads);
        Assert.Equal(0, fixture.ChannelReads);
        Assert.Equal(0, fixture.Store.Writes);
    }

    [Fact]
    public async Task PublishExistingPanel_ReusesIdentityAndCompletesDraftWithoutDuplicateSend()
    {
        var fixture = new Fixture();
        Assert.Equal(RoleMenuDraftCreateStatus.Created, fixture.Session.CreateDraft(
            1UL, 3UL, 4UL, "Roles", "", [10UL], RoleMenuSelectionMode.Multiple, out var draft));
        Assert.NotNull(draft);
        fixture.Settings = new RoleMenuSettings(draft.MenuId, "1", "4", "5", "Roles", "",
            ["10"], RoleMenuSelectionMode.Multiple);
        fixture.Store.Settings = fixture.Settings;
        Assert.Equal(RoleMenuDraftAccessStatus.Acquired,
            fixture.Session.TryBeginPublish(draft.Id, 1UL, 3UL, out _));

        var result = await fixture.Administration.PublishAsync(
            draft, fixture.Channel, 2UL, CancellationToken.None);

        Assert.Equal(RoleMenuPublicationStatus.Published, result.Status);
        Assert.Equal(5UL, result.MessageId);
        Assert.Equal(1, fixture.Store.Writes);
        Assert.Equal(RoleMenuDraftAccessStatus.NotFound,
            fixture.Session.TryBeginPublish(draft.Id, 1UL, 3UL, out _));
    }

    [Fact]
    public async Task Delete_RefreshesAdministratorPermissionBeforePanelOrPersistenceChanges()
    {
        var fixture = new Fixture { AdministratorCanManageRoles = false };
        var result = await fixture.Administration.DeleteAsync(
            fixture.Settings.Id, 1UL, 2UL, 3UL, CancellationToken.None);

        Assert.True(result.AuthorizationDenied);
        Assert.Equal(1, fixture.UserReads);
        Assert.Equal(0, fixture.ChannelReads);
        Assert.Equal(0, fixture.Store.Deletes);
        Assert.NotNull(fixture.Store.Settings);
    }

    private sealed class Fixture
    {
        internal Fixture()
        {
            Settings = new RoleMenuSettings(ObjectId.GenerateNewId(), "1", "4", "5", "Roles", "",
                ["10", "11"], RoleMenuSelectionMode.Multiple);
            Store = new Store { Settings = Settings };
            Session = new RoleMenuInteractionService(
                new RoleMenuRepository(Store, NullLogger<RoleMenuRepository>.Instance),
                new RoleMenuDraftRegistry(), new RoleMenuMutationCoordinator(), new InteractionExecutionContext());
            var roles = new[] { Role(1UL, 0), Role(10UL, 1), Role(11UL, 2), Role(20UL, 10) };
            var guild = Proxy<IGuild>((method, _) => method.Name switch
            {
                "get_Id" => 1UL,
                "get_OwnerId" => 7UL,
                "get_Roles" => roles,
                "get_EveryoneRole" => roles[0],
                _ => throw new NotSupportedException(method.Name)
            });
            IGuildUser User(ulong id) => Proxy<IGuildUser>((method, args) => method.Name switch
            {
                "get_Id" => id,
                "get_Guild" => guild,
                "get_GuildId" => 1UL,
                "get_RoleIds" => id == 2UL ? (IReadOnlyCollection<ulong>)[20UL] : MemberRoles[id],
                "get_GuildPermissions" => new GuildPermissions(manageRoles: id == 2UL || AdministratorCanManageRoles),
                "AddRoleAsync" => AddAsync(id, (ulong)args[0]!),
                _ => throw new NotSupportedException(method.Name)
            });
            var bot = User(2UL);
            var message = Proxy<IUserMessage>((method, _) => method.Name switch
            {
                "get_Id" => 5UL,
                "get_Author" => bot,
                "get_Components" => RoleMenuComponents.BuildPublicComponents(Settings.Id).Components,
                _ => throw new NotSupportedException(method.Name)
            });
            Channel = Proxy<ITextChannel>((method, _) => method.Name switch
            {
                "get_Id" => 4UL,
                "get_GuildId" => 1UL,
                "get_ChannelType" => ChannelType.Text,
                "GetMessageAsync" => Task.FromResult<IMessage>(message),
                _ => throw new NotSupportedException(method.Name)
            });
            var client = new DiscordRoleMenuClient(
                (_, id, options) =>
                {
                    options.CancelToken.ThrowIfCancellationRequested();
                    UserReads++;
                    return Task.FromResult<IGuildUser?>(User(id));
                },
                (_, options) =>
                {
                    options.CancelToken.ThrowIfCancellationRequested();
                    ChannelReads++;
                    return Task.FromResult<IChannel?>(Channel);
                });
            Members = new RoleMenuMemberService(Session, client, NullLogger<RoleMenuMemberService>.Instance);
            Administration = new RoleMenuAdministrationService(Session, client,
                NullLogger<RoleMenuAdministrationService>.Instance);
        }

        internal RoleMenuSettings Settings { get; set; }
        internal Store Store { get; }
        internal RoleMenuInteractionService Session { get; }
        internal RoleMenuMemberService Members { get; }
        internal RoleMenuAdministrationService Administration { get; }
        internal ITextChannel Channel { get; }
        internal bool AdministratorCanManageRoles { get; set; } = true;
        internal int UserReads { get; private set; }
        internal int ChannelReads { get; private set; }
        internal Dictionary<ulong, HashSet<ulong>> MemberRoles { get; } = new()
        {
            [3UL] = [99UL],
            [6UL] = [99UL]
        };
        internal ConcurrentQueue<(ulong Member, ulong Role)> Mutations { get; } = new();
        internal Func<ulong, Task> BeforeMutation { get; set; } = _ => Task.CompletedTask;

        private async Task AddAsync(ulong member, ulong role)
        {
            await BeforeMutation(member);
            MemberRoles[member].Add(role);
            Mutations.Enqueue((member, role));
        }

        private static IRole Role(ulong id, int position) => Proxy<IRole>((method, _) => method.Name switch
        {
            "get_Id" => id,
            "get_Name" => id.ToString(CultureInfo.InvariantCulture),
            "get_Position" => position,
            "get_IsManaged" => false,
            _ => throw new NotSupportedException(method.Name)
        });
    }

    private sealed class Store : IRoleMenuStore
    {
        internal RoleMenuSettings? Settings { get; set; }
        internal int Writes { get; private set; }
        internal int Deletes { get; private set; }
        public Task UpsertAsync(RoleMenuSettings settings, CancellationToken cancellationToken)
        {
            Writes++;
            Settings = settings;
            return Task.CompletedTask;
        }

        public Task<RoleMenuSettings?> GetByIdAsync(ObjectId id, string guildId, CancellationToken cancellationToken)
            => Task.FromResult(Settings?.Id == id && Settings.GuildId == guildId ? Settings : null);

        public Task<List<RoleMenuSettings>> GetByGuildAsync(string guildId, int maximumResults,
            CancellationToken cancellationToken)
            => Task.FromResult<List<RoleMenuSettings>>(Settings?.GuildId == guildId ? [Settings] : []);

        public Task<bool> DeleteAsync(ObjectId id, string guildId, CancellationToken cancellationToken)
        {
            Deletes++;
            Settings = null;
            return Task.FromResult(true);
        }
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, DiscordProxy>();
        ((DiscordProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class DiscordProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler(targetMethod!, args ?? []);
    }
}
