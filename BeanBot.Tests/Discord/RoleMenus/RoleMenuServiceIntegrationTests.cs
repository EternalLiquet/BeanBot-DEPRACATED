using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.Net;
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
        // The bounded coordinator deliberately serializes hash collisions; this case needs two active lanes.
        var secondMemberId = Enumerable.Range(6, 128).Select(value => (ulong)value).First(value =>
            RoleMenuMutationCoordinator.GetStripeIndex($"member:1:{value.ToString(CultureInfo.InvariantCulture)}")
            != RoleMenuMutationCoordinator.GetStripeIndex("member:1:3"));
        fixture.MemberRoles[secondMemberId] = [99UL];
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
                fixture.Settings.Id, 5UL, ["11"], 1UL, 4UL, 2UL, secondMemberId, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("11", second, StringComparison.Ordinal);
            Assert.Equal([11UL, 99UL], fixture.MemberRoles[secondMemberId].Order());
            Assert.Equal([99UL], fixture.MemberRoles[3UL]);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([10UL, 11UL, 99UL], fixture.MemberRoles[3UL].Order());
        Assert.Equal([11UL, 99UL], fixture.MemberRoles[secondMemberId].Order());
        Assert.Equal([(secondMemberId, 11UL), (3UL, 10UL), (3UL, 11UL)], fixture.Mutations);
    }

    [Fact]
    public async Task ForgedPanel_IsRejectedBeforeMembershipReadsOrMutations()
    {
        var fixture = new Fixture();
        var selector = await fixture.Members.LoadSelectorAsync(
            fixture.Settings.Id, 1UL, 4UL, 5UL, 999UL, 2UL, 3UL, true, CancellationToken.None);

        Assert.Null(selector.Components);
        Assert.Equal(
            "This role menu isn't working anymore. Ask a server admin to set it up again.",
            selector.Content);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(60)]
    public async Task LoadDeletionPage_ReachesEveryMenuForwardAndBackInBoundedPages(int menuCount)
    {
        var fixture = new Fixture();
        var allMenus = AddMenus(fixture, menuCount);

        var forward = new List<RoleMenuDeletionPage>();
        var page = await fixture.Administration.LoadDeletionPageAsync(1UL, null, CancellationToken.None);
        forward.Add(page);
        while (page.Next is { } next)
        {
            page = await fixture.Administration.LoadDeletionPageAsync(1UL, next, CancellationToken.None);
            forward.Add(page);
        }

        var backward = new List<RoleMenuDeletionPage> { page };
        while (page.Previous is { } previous)
        {
            page = await fixture.Administration.LoadDeletionPageAsync(1UL, previous, CancellationToken.None);
            backward.Add(page);
        }

        var listed = forward.SelectMany(candidate => candidate.Menus).Select(menu => menu.Id).ToList();
        Assert.Equal(
            allMenus.OrderByDescending(menu => menu.CreatedAtUtc)
                .ThenByDescending(menu => menu.Id)
                .Select(menu => menu.Id),
            listed);
        Assert.Equal(Math.Max(1, (menuCount + 24) / 25), forward.Count);
        Assert.All(forward, candidate => Assert.InRange(candidate.Menus.Count, menuCount == 0 ? 0 : 1, 25));
        Assert.Equal(
            forward.Select(candidate => candidate.Menus.Select(menu => menu.Id)),
            backward.AsEnumerable().Reverse().Select(candidate => candidate.Menus.Select(menu => menu.Id)));
        Assert.All(fixture.Store.PageReads, read => Assert.Equal(26, read.MaximumResults));
    }

    [Fact]
    public async Task LoadDeletionPage_StartsOverWhenMenusPastThePageEdgeWereDeleted()
    {
        var fixture = new Fixture();
        AddMenus(fixture, 3);
        var staleCursor = new RoleMenuPageCursor(
            DateTime.MinValue,
            ObjectId.GenerateNewId(),
            RoleMenuPageDirection.Older);

        var page = await fixture.Administration.LoadDeletionPageAsync(
            1UL,
            staleCursor,
            CancellationToken.None);

        Assert.Equal(3, page.Menus.Count);
        Assert.Null(page.Previous);
        Assert.Null(page.Next);
        Assert.Equal([staleCursor, null], fixture.Store.PageReads.Select(read => read.Cursor));
    }

    [Fact]
    public async Task FindMenuForMessage_UsesMessageIdentityAndThePanelButton()
    {
        var fixture = new Fixture();
        var sameMessageOtherMenu = new RoleMenuSettings(ObjectId.GenerateNewId(), "1", "4", "5",
            "Roles", "", ["10"], RoleMenuSelectionMode.Multiple);
        fixture.Store.OtherMenus.Add(sameMessageOtherMenu);

        var found = await fixture.Administration.FindMenuForMessageAsync(
            1UL, 4UL, 5UL, [fixture.Settings.Id], CancellationToken.None);
        var otherGuild = await fixture.Administration.FindMenuForMessageAsync(
            2UL, 4UL, 5UL, [fixture.Settings.Id], CancellationToken.None);
        var otherChannel = await fixture.Administration.FindMenuForMessageAsync(
            1UL, 8UL, 5UL, [fixture.Settings.Id], CancellationToken.None);
        var unknownButton = await fixture.Administration.FindMenuForMessageAsync(
            1UL, 4UL, 5UL, [ObjectId.GenerateNewId()], CancellationToken.None);

        Assert.Same(fixture.Settings, found);
        Assert.Null(otherGuild);
        Assert.Null(otherChannel);
        Assert.Null(unknownButton);
    }

    [Fact]
    public async Task InspectPanel_ReportsCurrentPanel()
    {
        var fixture = new Fixture();

        var state = await fixture.Administration.InspectPanelAsync(
            fixture.Settings, 1UL, 2UL, CancellationToken.None);

        Assert.Equal(RoleMenuPanelState.Current, state);
    }

    [Fact]
    public async Task InspectPanel_SeparatesConfirmedMissingFromInaccessibleAndUnavailable()
    {
        var fixture = new Fixture();

        fixture.ReadChannel = () => Task.FromResult<IChannel?>(null);
        Assert.Equal(RoleMenuPanelState.ChannelMissing, await Inspect());

        fixture.ReadChannel = null;
        fixture.ReadMessage = () => Task.FromResult<IMessage>(null!);
        Assert.Equal(RoleMenuPanelState.MessageMissing, await Inspect());

        fixture.ReadMessage = () => throw new HttpException(HttpStatusCode.NotFound, null);
        Assert.Equal(RoleMenuPanelState.MessageMissing, await Inspect());

        fixture.ReadMessage = () => throw new HttpException(HttpStatusCode.Forbidden, null);
        Assert.Equal(RoleMenuPanelState.Inaccessible, await Inspect());

        fixture.ReadMessage = null;
        fixture.ReadChannel = () => throw new HttpException(HttpStatusCode.Forbidden, null);
        Assert.Equal(RoleMenuPanelState.Inaccessible, await Inspect());

        fixture.ReadChannel = () => throw new HttpException(HttpStatusCode.ServiceUnavailable, null);
        Assert.Equal(RoleMenuPanelState.Unavailable, await Inspect());

        Task<RoleMenuPanelState> Inspect()
            => fixture.Administration.InspectPanelAsync(fixture.Settings, 1UL, 2UL, CancellationToken.None);
    }

    [Fact]
    public async Task InspectPanel_DoesNotSwallowCancellation()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Administration.InspectPanelAsync(fixture.Settings, 1UL, 2UL, cancellation.Token));
    }

    [Fact]
    public async Task DeleteConfirmed_ChangedMenuIsLeftUntouched()
    {
        var fixture = new Fixture();
        var shownVersion = RoleMenuDeletionTargets.GetVersion(fixture.Settings);
        fixture.Settings.UpdatedAtUtc = fixture.Settings.UpdatedAtUtc.AddSeconds(5);

        var deletion = await fixture.Administration.DeleteConfirmedAsync(
            fixture.Settings.Id, shownVersion, 1UL, 2UL, 3UL, CancellationToken.None);

        Assert.Equal(RoleMenuConfirmedDeletionStatus.Changed, deletion.Status);
        Assert.Null(deletion.Result);
        Assert.Equal(0, fixture.UserReads);
        Assert.Equal(0, fixture.ChannelReads);
        Assert.Equal(0, fixture.PanelDeletes);
        Assert.Equal(0, fixture.Store.Deletes);
        Assert.Equal(
            "That role menu changed. Run `/role-menu delete` to see it again.",
            RoleMenuPresentation.FormatConfirmedDeletion(deletion));
    }

    [Fact]
    public async Task DeleteConfirmed_MenuDeletedMeanwhileNeverFallsBackToAnotherMenu()
    {
        var fixture = new Fixture();
        var sameTitle = new RoleMenuSettings(ObjectId.GenerateNewId(), "1", "4", "6",
            fixture.Settings.Title, "", ["10"], RoleMenuSelectionMode.Multiple);
        fixture.Store.OtherMenus.Add(sameTitle);
        var deletedMenuId = fixture.Settings.Id;
        var shownVersion = RoleMenuDeletionTargets.GetVersion(fixture.Settings);
        fixture.Store.Settings = null;

        var deletion = await fixture.Administration.DeleteConfirmedAsync(
            deletedMenuId, shownVersion, 1UL, 2UL, 3UL, CancellationToken.None);

        Assert.Equal(RoleMenuConfirmedDeletionStatus.AlreadyDeleted, deletion.Status);
        Assert.Equal(0, fixture.ChannelReads);
        Assert.Equal(0, fixture.Store.Deletes);
        Assert.Contains(sameTitle, fixture.Store.OtherMenus);
        Assert.Equal(
            "That role menu was already deleted.",
            RoleMenuPresentation.FormatConfirmedDeletion(deletion));
    }

    [Fact]
    public async Task DeleteConfirmed_MatchingVersionReusesTheGuardedDeletionWorkflow()
    {
        var fixture = new Fixture();

        var deletion = await fixture.Administration.DeleteConfirmedAsync(
            fixture.Settings.Id,
            RoleMenuDeletionTargets.GetVersion(fixture.Settings),
            1UL, 2UL, 3UL, CancellationToken.None);

        Assert.Equal(RoleMenuConfirmedDeletionStatus.Attempted, deletion.Status);
        var result = Assert.IsType<RoleMenuDeletionResult>(deletion.Result);
        Assert.Equal(RoleMenuConfigurationDeletionStatus.Deleted, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.DeletedOrMissing, result.PanelStatus);
        Assert.Equal(1, fixture.PanelDeletes);
        Assert.Equal(1, fixture.Store.Deletes);
        Assert.Equal("Role menu deleted.", RoleMenuPresentation.FormatConfirmedDeletion(deletion));
    }

    [Fact]
    public async Task DeleteConfirmed_RechecksPermissionBeforeAnyChange()
    {
        var fixture = new Fixture { AdministratorCanManageRoles = false };

        var deletion = await fixture.Administration.DeleteConfirmedAsync(
            fixture.Settings.Id,
            RoleMenuDeletionTargets.GetVersion(fixture.Settings),
            1UL, 2UL, 3UL, CancellationToken.None);

        Assert.True(Assert.IsType<RoleMenuDeletionResult>(deletion.Result).AuthorizationDenied);
        Assert.Equal(0, fixture.ChannelReads);
        Assert.Equal(0, fixture.PanelDeletes);
        Assert.Equal(0, fixture.Store.Deletes);
    }

    [Fact]
    public async Task DeleteConfirmed_InaccessiblePanelKeepsTheSavedMenu()
    {
        var fixture = new Fixture
        {
            ReadMessage = () => throw new HttpException(HttpStatusCode.Forbidden, null)
        };

        var deletion = await fixture.Administration.DeleteConfirmedAsync(
            fixture.Settings.Id,
            RoleMenuDeletionTargets.GetVersion(fixture.Settings),
            1UL, 2UL, 3UL, CancellationToken.None);

        var result = Assert.IsType<RoleMenuDeletionResult>(deletion.Result);
        Assert.Equal(RoleMenuConfigurationDeletionStatus.Kept, result.ConfigurationStatus);
        Assert.Equal(0, fixture.PanelDeletes);
        Assert.Equal(0, fixture.Store.Deletes);
        Assert.NotNull(fixture.Store.Settings);
    }

    [Fact]
    public async Task DeleteConfirmed_ConfirmedMissingPanelStillCleansUpTheSavedMenu()
    {
        var fixture = new Fixture
        {
            ReadMessage = () => Task.FromResult<IMessage>(null!)
        };

        var deletion = await fixture.Administration.DeleteConfirmedAsync(
            fixture.Settings.Id,
            RoleMenuDeletionTargets.GetVersion(fixture.Settings),
            1UL, 2UL, 3UL, CancellationToken.None);

        var result = Assert.IsType<RoleMenuDeletionResult>(deletion.Result);
        Assert.Equal(RoleMenuConfigurationDeletionStatus.Deleted, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionIssue.MessageMissing, result.PanelIssue);
        Assert.Equal(1, fixture.Store.Deletes);
    }

    [Theory]
    [InlineData(null, "Give the menu a title.")]
    [InlineData("   ", "Give the menu a title.")]
    [InlineData("​ㅤ", "Give the menu a title.")]
    [InlineData("\U000E0100", "Give the menu a title.")]
    public async Task CreatePreview_RejectsMissingOrVisuallyEmptyTitles(string? title, string expected)
    {
        var fixture = new Fixture();

        var preview = await fixture.Administration.CreatePreviewAsync(
            new RoleMenuCreateRequest(title!, null, "multiple", 4UL, 1UL, ChannelType.Text, [10UL]),
            1UL, 3UL, 2UL, CancellationToken.None);

        Assert.Null(preview.Draft);
        Assert.Equal(expected, preview.Content);
        Assert.Equal(0, fixture.ChannelReads);
    }

    [Fact]
    public async Task CreatePreview_EnforcesTitleLengthAfterTrimming()
    {
        var fixture = new Fixture();
        var tooLong = await fixture.Administration.CreatePreviewAsync(
            new RoleMenuCreateRequest(new string('a', 101), null, "multiple", 4UL, 1UL,
                ChannelType.Text, [10UL]),
            1UL, 3UL, 2UL, CancellationToken.None);
        var paddedButValid = await fixture.Administration.CreatePreviewAsync(
            new RoleMenuCreateRequest("  " + new string('a', 100) + "  ", null, "multiple", 4UL, 1UL,
                ChannelType.Text, [10UL]),
            1UL, 3UL, 2UL, CancellationToken.None);

        Assert.Equal("The title can't be longer than 100 characters.", tooLong.Content);
        Assert.DoesNotContain("title", paddedButValid.Content, StringComparison.OrdinalIgnoreCase);
    }

    private static List<RoleMenuSettings> AddMenus(Fixture fixture, int menuCount)
    {
        fixture.Store.Settings = null;
        var sharedCreationTime = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < menuCount; index++)
        {
            var menu = new RoleMenuSettings(ObjectId.GenerateNewId(), "1", "4",
                (100 + index).ToString(CultureInfo.InvariantCulture), "Same title", "", ["10"],
                RoleMenuSelectionMode.Multiple)
            {
                // Half of the menus share one creation time to exercise the ID tie-breaker.
                CreatedAtUtc = index % 2 == 0 ? sharedCreationTime : sharedCreationTime.AddMinutes(-index)
            };
            fixture.Store.OtherMenus.Add(menu);
        }

        fixture.Store.OtherMenus.Add(new RoleMenuSettings(ObjectId.GenerateNewId(), "999", "4", "7",
            "Other guild", "", ["10"], RoleMenuSelectionMode.Multiple));
        return [.. fixture.Store.OtherMenus.Where(menu => menu.GuildId == "1")];
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
                "DeleteAsync" => DeletePanelMessage(),
                _ => throw new NotSupportedException(method.Name)
            });
            Channel = Proxy<ITextChannel>((method, _) => method.Name switch
            {
                "get_Id" => 4UL,
                "get_GuildId" => 1UL,
                "get_ChannelType" => ChannelType.Text,
                "GetMessageAsync" => ReadMessage?.Invoke() ?? Task.FromResult<IMessage>(message),
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
                    return ReadChannel?.Invoke() ?? Task.FromResult<IChannel?>(Channel);
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
        internal Func<Task<IChannel?>>? ReadChannel { get; set; }
        internal Func<Task<IMessage>>? ReadMessage { get; set; }
        internal int PanelDeletes { get; private set; }
        internal int UserReads { get; private set; }
        internal int ChannelReads { get; private set; }
        internal Dictionary<ulong, HashSet<ulong>> MemberRoles { get; } = new()
        {
            [3UL] = [99UL],
            [6UL] = [99UL]
        };
        internal ConcurrentQueue<(ulong Member, ulong Role)> Mutations { get; } = new();
        internal Func<ulong, Task> BeforeMutation { get; set; } = _ => Task.CompletedTask;

        private Task DeletePanelMessage()
        {
            PanelDeletes++;
            return Task.CompletedTask;
        }

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

        public Task<List<RoleMenuSettings>> GetByMessageAsync(string guildId, string channelId,
            string messageId, int maximumResults, CancellationToken cancellationToken)
            => Task.FromResult(AllMenus()
                .Where(menu => menu.GuildId == guildId && menu.ChannelId == channelId
                               && menu.MessageId == messageId)
                .Take(maximumResults)
                .ToList());

        public Task<List<RoleMenuSettings>> GetPageAsync(string guildId, RoleMenuPageCursor? cursor,
            int maximumResults, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageReads.Add((cursor, maximumResults));
            var newestFirst = AllMenus()
                .Where(menu => menu.GuildId == guildId)
                .OrderByDescending(menu => menu.CreatedAtUtc)
                .ThenByDescending(menu => menu.Id)
                .ToList();
            if (cursor is not { } boundary)
            {
                return Task.FromResult(newestFirst.Take(maximumResults).ToList());
            }

            var boundaryKey = (boundary.CreatedAtUtc, boundary.MenuId);
            int Compare(RoleMenuSettings menu)
                => (menu.CreatedAtUtc, menu.Id).CompareTo(boundaryKey);
            return Task.FromResult<List<RoleMenuSettings>>(boundary.Direction == RoleMenuPageDirection.Older
                ? [.. newestFirst.Where(menu => Compare(menu) < 0).Take(maximumResults)]
                : [.. newestFirst.Where(menu => Compare(menu) > 0).TakeLast(maximumResults)]);
        }

        public Task<bool> DeleteAsync(ObjectId id, string guildId, CancellationToken cancellationToken)
        {
            Deletes++;
            Settings = null;
            return Task.FromResult(true);
        }

        internal List<RoleMenuSettings> OtherMenus { get; } = [];
        internal List<(RoleMenuPageCursor? Cursor, int MaximumResults)> PageReads { get; } = [];

        private IEnumerable<RoleMenuSettings> AllMenus()
            => Settings is null ? OtherMenus : OtherMenus.Prepend(Settings);
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
