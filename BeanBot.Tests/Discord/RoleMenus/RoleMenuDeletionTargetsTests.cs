using System.Net;
using System.Reflection;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.Net;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuDeletionTargetsTests
{
    private const ulong GuildId = 1UL;
    private const ulong ChannelId = 2UL;
    private const ulong MessageId = 3UL;
    private const ulong BotUserId = 9UL;

    [Fact]
    public void BuildPage_EmptyGuildHasNoMenusOrControls()
    {
        var page = RoleMenuDeletionTargets.BuildPage([], null, 25);

        Assert.Empty(page.Menus);
        Assert.Null(page.Previous);
        Assert.Null(page.Next);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    public void BuildPage_FirstPageThatFitsHasNoPageControls(int menuCount)
    {
        var fetched = CreateNewestFirst(menuCount);

        var page = RoleMenuDeletionTargets.BuildPage(fetched, null, 25);

        Assert.Equal(fetched, page.Menus);
        Assert.Null(page.Previous);
        Assert.Null(page.Next);
    }

    [Fact]
    public void BuildPage_TwentySixMenusOfferNextFromTheTwentyFifth()
    {
        var fetched = CreateNewestFirst(26);

        var page = RoleMenuDeletionTargets.BuildPage(fetched, null, 25);

        Assert.Equal(fetched.Take(25), page.Menus);
        Assert.Null(page.Previous);
        var next = Assert.IsType<RoleMenuPageCursor>(page.Next);
        Assert.Equal(RoleMenuPageDirection.Older, next.Direction);
        Assert.Equal(fetched[24].Id, next.MenuId);
        Assert.Equal(fetched[24].CreatedAtUtc, next.CreatedAtUtc);
    }

    [Fact]
    public void BuildPage_OlderPageAlwaysOffersPreviousAndOnlyOffersNextWhenMoreExist()
    {
        var fetched = CreateNewestFirst(3);
        var cursor = new RoleMenuPageCursor(DateTime.UnixEpoch, ObjectId.GenerateNewId(),
            RoleMenuPageDirection.Older);

        var page = RoleMenuDeletionTargets.BuildPage(fetched, cursor, 25);

        Assert.Equal(fetched, page.Menus);
        var previous = Assert.IsType<RoleMenuPageCursor>(page.Previous);
        Assert.Equal(RoleMenuPageDirection.Newer, previous.Direction);
        Assert.Equal(fetched[0].Id, previous.MenuId);
        Assert.Null(page.Next);
    }

    [Fact]
    public void BuildPage_NewerPageDropsTheExtraNewestMenuAndKeepsNext()
    {
        var fetched = CreateNewestFirst(26);
        var cursor = new RoleMenuPageCursor(DateTime.UnixEpoch, ObjectId.GenerateNewId(),
            RoleMenuPageDirection.Newer);

        var page = RoleMenuDeletionTargets.BuildPage(fetched, cursor, 25);

        Assert.Equal(fetched.Skip(1), page.Menus);
        Assert.Equal(fetched[1].Id, Assert.IsType<RoleMenuPageCursor>(page.Previous).MenuId);
        Assert.Equal(fetched[25].Id, Assert.IsType<RoleMenuPageCursor>(page.Next).MenuId);
    }

    [Fact]
    public void BuildPage_NewerPageReachingTheStartHasNoPrevious()
    {
        var fetched = CreateNewestFirst(4);
        var cursor = new RoleMenuPageCursor(DateTime.UnixEpoch, ObjectId.GenerateNewId(),
            RoleMenuPageDirection.Newer);

        var page = RoleMenuDeletionTargets.BuildPage(fetched, cursor, 25);

        Assert.Equal(fetched, page.Menus);
        Assert.Null(page.Previous);
        Assert.NotNull(page.Next);
    }

    [Fact]
    public void CheckMessage_AcceptsOnlyBotRoleMenuInTheCommandChannel()
    {
        IReadOnlyCollection<ObjectId> buttons = [ObjectId.GenerateNewId()];

        Assert.Equal(
            RoleMenuMessageTargetIssue.None,
            RoleMenuDeletionTargets.CheckMessage(ChannelId, BotUserId, ChannelId, BotUserId, buttons));
        Assert.Equal(
            RoleMenuMessageTargetIssue.WrongLocation,
            RoleMenuDeletionTargets.CheckMessage(ChannelId, BotUserId, 77UL, BotUserId, buttons));
        Assert.Equal(
            RoleMenuMessageTargetIssue.WrongLocation,
            RoleMenuDeletionTargets.CheckMessage(ChannelId, BotUserId, 0UL, BotUserId, buttons));
        Assert.Equal(
            RoleMenuMessageTargetIssue.NotBotMessage,
            RoleMenuDeletionTargets.CheckMessage(ChannelId, BotUserId, ChannelId, 44UL, buttons));
        Assert.Equal(
            RoleMenuMessageTargetIssue.NotRoleMenu,
            RoleMenuDeletionTargets.CheckMessage(ChannelId, BotUserId, ChannelId, BotUserId, []));
    }

    [Fact]
    public void MatchSavedMenu_ResolvesTheMenuNamedByThePanelButton()
    {
        var target = CreateSettings("Games");
        var sameMessageOtherMenu = CreateSettings("Games");

        var match = RoleMenuDeletionTargets.MatchSavedMenu(
            [sameMessageOtherMenu, target],
            [target.Id],
            GuildId,
            ChannelId,
            MessageId);

        Assert.Same(target, match);
    }

    [Fact]
    public void MatchSavedMenu_RejectsWrongGuildChannelOrMessage()
    {
        var target = CreateSettings("Games");

        Assert.Null(RoleMenuDeletionTargets.MatchSavedMenu([target], [target.Id], 99UL, ChannelId, MessageId));
        Assert.Null(RoleMenuDeletionTargets.MatchSavedMenu([target], [target.Id], GuildId, 99UL, MessageId));
        Assert.Null(RoleMenuDeletionTargets.MatchSavedMenu([target], [target.Id], GuildId, ChannelId, 99UL));
    }

    [Fact]
    public void MatchSavedMenu_MissingOrUnrelatedRecordIsNotSaved()
    {
        var target = CreateSettings("Games");

        Assert.Null(RoleMenuDeletionTargets.MatchSavedMenu([], [target.Id], GuildId, ChannelId, MessageId));
        Assert.Null(RoleMenuDeletionTargets.MatchSavedMenu(
            [target],
            [ObjectId.GenerateNewId()],
            GuildId,
            ChannelId,
            MessageId));
    }

    [Fact]
    public void MatchSavedMenu_AmbiguousButtonsResolveNothing()
    {
        var first = CreateSettings("Games");
        var second = CreateSettings("Games");

        Assert.Null(RoleMenuDeletionTargets.MatchSavedMenu(
            [first, second],
            [first.Id, second.Id],
            GuildId,
            ChannelId,
            MessageId));
    }

    [Fact]
    public void GetManageButtonMenuIds_ReadsOnlyRoleMenuManageButtons()
    {
        var menuId = ObjectId.GenerateNewId();
        var components = new ComponentBuilder()
            .WithButton("Choose your roles", RoleMenuCustomIds.Manage(menuId))
            .WithButton("Other", "other-feature:manage:" + ObjectId.GenerateNewId())
            .WithButton("Broken", "role-menu:manage:not-an-id")
            .WithButton("Link", style: ButtonStyle.Link, url: "https://example.com")
            .Build();

        var menuIds = RoleMenuDeletionTargets.GetManageButtonMenuIds(CreateMessage(components));

        Assert.Equal([menuId], menuIds);
        Assert.Empty(RoleMenuDeletionTargets.GetManageButtonMenuIds(
            CreateMessage(MessageComponent.Empty)));
    }

    [Fact]
    public void ClassifyPanel_DistinguishesCurrentMissingAndForeignMessages()
    {
        var settings = CreateSettings("Games");

        Assert.Equal(RoleMenuPanelState.Current, Classify(Found(BotUserId, hasManageButton: true)));
        Assert.Equal(RoleMenuPanelState.NotAPanel, Classify(Found(44UL, hasManageButton: true)));
        Assert.Equal(RoleMenuPanelState.NotAPanel, Classify(Found(BotUserId, hasManageButton: false)));
        Assert.Equal(
            RoleMenuPanelState.NotAPanel,
            Classify(new RoleMenuPanelLookupResult(
                RoleMenuPanelLookupStatus.Found,
                new RoleMenuPanelSnapshot(GuildId, ChannelId, 99UL, BotUserId, true))));
        Assert.Equal(
            RoleMenuPanelState.MessageMissing,
            Classify(new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing)));
        Assert.Equal(
            RoleMenuPanelState.ChannelMissing,
            Classify(new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.ChannelMissing)));
        Assert.Equal(
            RoleMenuPanelState.NotAPanel,
            Classify(new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.UnexpectedChannelType)));

        RoleMenuPanelState Classify(RoleMenuPanelLookupResult lookup)
            => RoleMenuDeletionTargets.ClassifyPanel(lookup, settings, GuildId, BotUserId);
    }

    [Fact]
    public void ClassifyPanelFailure_PermissionErrorsAreInaccessibleNotMissing()
    {
        Assert.Equal(
            RoleMenuPanelState.Inaccessible,
            RoleMenuDeletionTargets.ClassifyPanelFailure(
                new HttpException(HttpStatusCode.Forbidden, null)));
        Assert.Equal(
            RoleMenuPanelState.Inaccessible,
            RoleMenuDeletionTargets.ClassifyPanelFailure(
                new HttpException(HttpStatusCode.BadRequest, null, DiscordErrorCode.MissingPermissions)));
        Assert.Equal(
            RoleMenuPanelState.Unavailable,
            RoleMenuDeletionTargets.ClassifyPanelFailure(
                new HttpException(HttpStatusCode.InternalServerError, null)));
        Assert.Equal(
            RoleMenuPanelState.Unavailable,
            RoleMenuDeletionTargets.ClassifyPanelFailure(new TimeoutException()));
    }

    [Theory]
    [InlineData((int)RoleMenuPanelState.Current, true)]
    [InlineData((int)RoleMenuPanelState.MessageMissing, true)]
    [InlineData((int)RoleMenuPanelState.ChannelMissing, true)]
    [InlineData((int)RoleMenuPanelState.NotAPanel, true)]
    [InlineData((int)RoleMenuPanelState.Inaccessible, false)]
    [InlineData((int)RoleMenuPanelState.Unavailable, false)]
    public void CanDelete_RequiresAConfirmedPanelState(int panelStateValue, bool expected)
    {
        Assert.Equal(expected, RoleMenuDeletionTargets.CanDelete((RoleMenuPanelState)panelStateValue));
    }

    [Fact]
    public void IsControlExpired_ExpiresAfterTheManagementLifetime()
    {
        var created = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.False(RoleMenuDeletionTargets.IsControlExpired(created, created));
        Assert.False(RoleMenuDeletionTargets.IsControlExpired(
            created,
            created + RoleMenuConstants.ManagementControlLifetime));
        Assert.True(RoleMenuDeletionTargets.IsControlExpired(
            created,
            created + RoleMenuConstants.ManagementControlLifetime + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void GetVersion_ChangesWhenTheSavedMenuIsRewritten()
    {
        var settings = CreateSettings("Games");
        settings.UpdatedAtUtc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var shown = RoleMenuDeletionTargets.GetVersion(settings);

        settings.UpdatedAtUtc = settings.UpdatedAtUtc.AddMilliseconds(1);

        Assert.NotEqual(shown, RoleMenuDeletionTargets.GetVersion(settings));
    }

    private static RoleMenuPanelLookupResult Found(ulong authorId, bool hasManageButton)
        => new(
            RoleMenuPanelLookupStatus.Found,
            new RoleMenuPanelSnapshot(GuildId, ChannelId, MessageId, authorId, hasManageButton));

    private static List<RoleMenuSettings> CreateNewestFirst(int count)
    {
        var created = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        return [.. Enumerable.Range(0, count)
            .Select(index =>
            {
                var settings = CreateSettings($"Menu {index}");
                settings.CreatedAtUtc = created.AddMinutes(-index);
                return settings;
            })];
    }

    private static RoleMenuSettings CreateSettings(string title)
        => new(
            ObjectId.GenerateNewId(),
            "1",
            "2",
            "3",
            title,
            string.Empty,
            ["10"],
            RoleMenuSelectionMode.Multiple);

    private static IMessage CreateMessage(MessageComponent components)
    {
        var message = DispatchProxy.Create<IMessage, ComponentMessageProxy>();
        ((ComponentMessageProxy)(object)message).Components = components.Components;
        return message;
    }

    public class ComponentMessageProxy : DispatchProxy
    {
        public IReadOnlyCollection<IMessageComponent> Components { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_Components"
                ? Components
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
