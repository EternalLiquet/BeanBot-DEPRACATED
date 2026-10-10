using System.Reflection;
using System.Text.RegularExpressions;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuComponentsTests
{
    [Fact]
    public void BuildPublicComponents_ContainsStableManageIdentifier()
    {
        var menuId = ObjectId.GenerateNewId();

        var components = RoleMenuComponents.BuildPublicComponents(menuId);

        var row = Assert.IsType<ActionRowComponent>(Assert.Single(components.Components));
        var button = Assert.IsType<ButtonComponent>(Assert.Single(row.Components));
        Assert.Equal("Choose your roles", button.Label);
        Assert.Equal(RoleMenuCustomIds.Manage(menuId), button.CustomId);
        Assert.Equal(ButtonStyle.Primary, button.Style);
    }

    [Fact]
    public void BuildPublicEmbed_ShowsModeWithoutAnyInternalId()
    {
        var embed = RoleMenuComponents.BuildPublicEmbed(
            "Games",
            string.Empty,
            RoleMenuSelectionMode.Exclusive);

        Assert.True(embed.Footer.HasValue);
        var footer = embed.Footer.GetValueOrDefault();
        Assert.Equal("Role menu • Choose one role", footer.Text);
        Assert.DoesNotContain("ID", footer.Text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(embed.Description));
        AssertNoRawIds(embed.Title, embed.Description, footer.Text);
    }

    [Fact]
    public void BuildPublicEmbed_MultipleModePreservesNonblankDescription()
    {
        var embed = RoleMenuComponents.BuildPublicEmbed(
            "Games",
            "Choose the games you play.",
            RoleMenuSelectionMode.Multiple);

        Assert.Equal("Games", embed.Title);
        Assert.Equal("Choose the games you play.", embed.Description);
        Assert.True(embed.Footer.HasValue);
        var footer = embed.Footer.GetValueOrDefault();
        Assert.Equal("Role menu • Choose as many as you like", footer.Text);
    }

    [Fact]
    public void BuildPreviewEmbed_MultipleModeRendersRolesAndTargetChannel()
    {
        var draft = CreateDraft(
            RoleMenuSelectionMode.Multiple,
            "Choose the games you play.");

        var embed = RoleMenuComponents.BuildPreviewEmbed(draft, CreateRoles());

        Assert.Equal(draft.Title, embed.Title);
        Assert.Equal(draft.Description, embed.Description);
        var roles = Assert.Single(embed.Fields, field => field.Name == "Roles");
        Assert.Contains("<@&10>", roles.Value, StringComparison.Ordinal);
        Assert.Contains("<@&20>", roles.Value, StringComparison.Ordinal);
        Assert.Equal(
            "Any number",
            Assert.Single(embed.Fields, field => field.Name == "Members can choose").Value);
        Assert.Equal(
            $"<#{draft.TargetChannelId}>",
            Assert.Single(embed.Fields, field => field.Name == "Channel").Value);
    }

    [Fact]
    public void BuildPreviewEmbed_ExclusiveModeWithNoRolesUsesFallbacks()
    {
        var draft = CreateDraft(RoleMenuSelectionMode.Exclusive, " ");
        IReadOnlyCollection<RoleMenuRoleSnapshot> noRoles = [];

        var embed = RoleMenuComponents.BuildPreviewEmbed(draft, noRoles);

        Assert.Equal(
            "Choose the roles you want. You can change them any time.",
            embed.Description);
        Assert.Equal(
            "None of these roles can be used anymore.",
            Assert.Single(embed.Fields, field => field.Name == "Roles").Value);
        Assert.Equal(
            "One role",
            Assert.Single(embed.Fields, field => field.Name == "Members can choose").Value);
        Assert.Equal("Preview • Only you can see this", embed.Footer.GetValueOrDefault().Text);
    }

    [Fact]
    public void BuildPreviewComponents_ContainsPublishAndCancelActions()
    {
        var draftId = Guid.NewGuid();

        var buttons = GetButtons(RoleMenuComponents.BuildPreviewComponents(draftId));

        var publish = Assert.Single(
            buttons,
            button => button.CustomId == RoleMenuCustomIds.Publish(draftId));
        Assert.Equal("Publish menu", publish.Label);
        Assert.Equal(ButtonStyle.Success, publish.Style);
        var cancel = Assert.Single(
            buttons,
            button => button.CustomId == RoleMenuCustomIds.CancelPublish(draftId));
        Assert.Equal("Cancel", cancel.Label);
        Assert.Equal(ButtonStyle.Secondary, cancel.Style);
    }

    [Fact]
    public void BuildPreviewEmbed_NullArgumentsThrow()
    {
        var draft = CreateDraft(RoleMenuSelectionMode.Multiple);
        var roles = CreateRoles();

        Assert.Equal(
            "draft",
            Assert.Throws<ArgumentNullException>(
                () => RoleMenuComponents.BuildPreviewEmbed(null!, roles)).ParamName);
        Assert.Equal(
            "roles",
            Assert.Throws<ArgumentNullException>(
                () => RoleMenuComponents.BuildPreviewEmbed(draft, null!)).ParamName);
    }

    [Fact]
    public void BuildMemberSelector_MultipleMode_DefaultsToCurrentConfiguredRolesOnly()
    {
        var settings = CreateSettings(RoleMenuSelectionMode.Multiple);
        var parsed = CreateParsedSettings();
        var selector = RoleMenuComponents.BuildMemberSelector(
            settings,
            parsed,
            CreateRoles(),
            [10UL, 99UL],
            123UL);

        var select = GetSelect(selector.Components);
        Assert.Equal(0, select.MinValues);
        Assert.Equal(2, select.MaxValues);
        Assert.Equal(true, Assert.Single(select.Options, option => option.Value == "10").IsDefault);
        Assert.Equal(false, Assert.Single(select.Options, option => option.Value == "20").IsDefault);
        Assert.DoesNotContain(select.Options, option => option.Value == "99");
        Assert.False(selector.HadConflictingSingleSelection);
        AssertClearButton(selector.Components, settings.Id, 123UL, parsed.MessageId);
    }

    [Fact]
    public void BuildMemberSelector_SingleMode_BoundsSelectionAndRepairsConflictingDefaults()
    {
        var settings = CreateSettings(RoleMenuSelectionMode.Exclusive);
        var parsed = CreateParsedSettings();

        var selector = RoleMenuComponents.BuildMemberSelector(
            settings,
            parsed,
            CreateRoles(),
            [10UL, 20UL],
            123UL);

        var select = GetSelect(selector.Components);
        Assert.Equal(1, select.MaxValues);
        Assert.Single(select.Options, option => option.IsDefault == true);
        Assert.True(selector.HadConflictingSingleSelection);
    }

    [Fact]
    public void BuildMemberSelector_ExclusiveModePreservesOneConfiguredDefault()
    {
        var settings = CreateSettings(RoleMenuSelectionMode.Exclusive);
        var parsed = CreateParsedSettings();

        var selector = RoleMenuComponents.BuildMemberSelector(
            settings,
            parsed,
            CreateRoles(),
            [20UL, 99UL],
            123UL);

        var select = GetSelect(selector.Components);
        Assert.Equal(0, select.MinValues);
        Assert.Equal(1, select.MaxValues);
        Assert.Equal(
            RoleMenuCustomIds.Save(settings.Id, 123UL, parsed.MessageId),
            select.CustomId);
        Assert.Equal(true, Assert.Single(
            select.Options,
            option => option.Value == "20").IsDefault);
        Assert.False(selector.HadConflictingSingleSelection);
    }

    [Fact]
    public void BuildMemberSelector_NullArgumentsThrow()
    {
        var settings = CreateSettings(RoleMenuSelectionMode.Multiple);
        var parsed = CreateParsedSettings();
        var roles = CreateRoles();
        IReadOnlyCollection<ulong> currentRoleIds = [];

        Assert.Equal(
            "settings",
            Assert.Throws<ArgumentNullException>(() =>
                RoleMenuComponents.BuildMemberSelector(
                    null!,
                    parsed,
                    roles,
                    currentRoleIds,
                    123UL)).ParamName);
        Assert.Equal(
            "parsed",
            Assert.Throws<ArgumentNullException>(() =>
                RoleMenuComponents.BuildMemberSelector(
                    settings,
                    null!,
                    roles,
                    currentRoleIds,
                    123UL)).ParamName);
        Assert.Equal(
            "roles",
            Assert.Throws<ArgumentNullException>(() =>
                RoleMenuComponents.BuildMemberSelector(
                    settings,
                    parsed,
                    null!,
                    currentRoleIds,
                    123UL)).ParamName);
        Assert.Equal(
            "currentRoleIds",
            Assert.Throws<ArgumentNullException>(() =>
                RoleMenuComponents.BuildMemberSelector(
                    settings,
                    parsed,
                    roles,
                    null!,
                    123UL)).ParamName);
    }

    [Fact]
    public void FormatCreatedAt_ShowsRelativeAge()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var created = now.AddHours(-2).AddMinutes(-5);

        Assert.Equal("Created 2 hours ago", RoleMenuComponents.FormatCreatedAt(created, now));
    }

    [Theory]
    [InlineData(0, "Created just now")]
    [InlineData(59, "Created just now")]
    [InlineData(60, "Created 1 minute ago")]
    [InlineData(120, "Created 2 minutes ago")]
    [InlineData(3599, "Created 59 minutes ago")]
    [InlineData(3600, "Created 1 hour ago")]
    [InlineData(7200, "Created 2 hours ago")]
    [InlineData(86399, "Created 23 hours ago")]
    [InlineData(86400, "Created 1 day ago")]
    [InlineData(172800, "Created 2 days ago")]
    public void FormatCreatedAt_UsesWholeAgeUnits(int elapsedSeconds, string expected)
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, RoleMenuComponents.FormatCreatedAt(now.AddSeconds(-elapsedSeconds), now));
    }

    [Fact]
    public void FormatCreatedAt_HandlesUnknownFutureAndOldDates()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("Creation date unknown", RoleMenuComponents.FormatCreatedAt(default, now));
        Assert.Equal("Creation date unknown", RoleMenuComponents.FormatCreatedAt(now.AddSeconds(1), now));
        Assert.Equal("Created 26 years ago", RoleMenuComponents.FormatCreatedAt(now.AddYears(-26), now));
    }

    [Fact]
    public void BuildDeleteSelector_RendersUntitledAndDatedMenus()
    {
        const ulong userId = 123UL;
        var stale = CreateSettings(RoleMenuSelectionMode.Multiple, " ");
        var normal = CreateSettings(RoleMenuSelectionMode.Exclusive, "Music");
        normal.CreatedAtUtc = new DateTime(2026, 8, 20, 14, 5, 0, DateTimeKind.Utc);

        var select = GetSelect(RoleMenuComponents.BuildDeleteSelector(
            userId,
            new RoleMenuDeletionPage([stale, normal], null, null),
            channelId => channelId == 2UL ? "roles" : null,
            new DateTime(2026, 10, 10, 14, 5, 0, DateTimeKind.Utc)));

        Assert.Equal(RoleMenuCustomIds.DeleteSelect(userId), select.CustomId);
        Assert.Equal(1, select.MinValues);
        Assert.Equal(1, select.MaxValues);
        var staleOption = Assert.Single(
            select.Options,
            option => option.Value == stale.Id.ToString());
        Assert.Equal("Untitled role menu", staleOption.Label);
        Assert.Equal("#roles • Creation date unknown", staleOption.Description);
        var normalOption = Assert.Single(
            select.Options,
            option => option.Value == normal.Id.ToString());
        Assert.Equal("Music", normalOption.Label);
        Assert.Equal("#roles • Created 51 days ago", normalOption.Description);
    }

    [Fact]
    public void BuildDeleteSelector_RecalculatesAgeWhenRenderedAgain()
    {
        var menu = CreateSettings(RoleMenuSelectionMode.Multiple, "Games");
        var created = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc);
        menu.CreatedAtUtc = created;
        var page = new RoleMenuDeletionPage([menu], null, null);

        var first = GetSelect(RoleMenuComponents.BuildDeleteSelector(
            123UL, page, _ => "roles", created.AddMinutes(2)));
        var later = GetSelect(RoleMenuComponents.BuildDeleteSelector(
            123UL, page, _ => "roles", created.AddHours(2)));

        Assert.Equal("#roles • Created 2 minutes ago", Assert.Single(first.Options).Description);
        Assert.Equal("#roles • Created 2 hours ago", Assert.Single(later.Options).Description);
    }

    [Fact]
    public void BuildDeleteSelector_DistinguishesSameTitleChannelAndDay()
    {
        var morning = CreateSettings(RoleMenuSelectionMode.Multiple, "Games");
        var evening = CreateSettings(RoleMenuSelectionMode.Multiple, "Games");
        morning.CreatedAtUtc = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        evening.CreatedAtUtc = new DateTime(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc);

        var select = GetSelect(RoleMenuComponents.BuildDeleteSelector(
            123UL,
            new RoleMenuDeletionPage([morning, evening], null, null),
            _ => "roles",
            new DateTime(2026, 10, 10, 18, 30, 0, DateTimeKind.Utc)));

        Assert.All(select.Options, option => Assert.Equal("Games", option.Label));
        var descriptions = select.Options.Select(option => option.Description).ToArray();
        Assert.NotEqual(descriptions[0], descriptions[1]);
        Assert.All(select.Options, option => Assert.StartsWith(
            "#roles • Created 9 days", option.Description, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("​")]
    [InlineData("ㅤㅤ")]
    [InlineData("\t ⠀")]
    public void GetDisplayTitle_ShowsUntitledForVisuallyEmptyTitles(string title)
    {
        Assert.Equal("Untitled role menu", RoleMenuComponents.GetDisplayTitle(title));
    }

    [Fact]
    public void GetDisplayTitle_KeepsLegitimateTitlesExceptOuterWhitespace()
    {
        Assert.Equal("Game Roles 🎮", RoleMenuComponents.GetDisplayTitle("  Game Roles 🎮 "));
        Assert.Equal("Untitled role menu", RoleMenuComponents.GetDisplayTitle(null));
    }

    [Fact]
    public void BuildDeleteSelector_KeepsSameTitledMenusSeparateAndIdentifiable()
    {
        var first = CreateRealisticSettings("Game Roles", "234567890123456789");
        first.CreatedAtUtc = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var sameChannel = CreateRealisticSettings("Game Roles", "234567890123456789");
        sameChannel.CreatedAtUtc = new DateTime(2026, 10, 2, 18, 30, 0, DateTimeKind.Utc);
        var otherChannel = CreateRealisticSettings("Game Roles", "334567890123456789");
        otherChannel.CreatedAtUtc = first.CreatedAtUtc;

        var select = GetSelect(RoleMenuComponents.BuildDeleteSelector(
            123UL,
            new RoleMenuDeletionPage([sameChannel, first, otherChannel], null, null),
            channelId => channelId == 234567890123456789UL ? "roles" : "games",
            new DateTime(2026, 10, 10, 18, 30, 0, DateTimeKind.Utc)));

        Assert.Equal(3, select.Options.Count);
        Assert.All(select.Options, option => Assert.Equal("Game Roles", option.Label));
        Assert.Equal(3, select.Options.Select(option => option.Value).Distinct().Count());
        Assert.Equal(3, select.Options.Select(option => option.Description).Distinct().Count());
        Assert.All(select.Options, option => AssertNoRawIds(option.Label, option.Description));
    }

    [Fact]
    public void BuildDeleteSelector_ShowsOnlyAvailablePageControlsAndCancel()
    {
        const ulong userId = 123UL;
        var menus = Enumerable.Range(0, 25)
            .Select(index => CreateSettings(RoleMenuSelectionMode.Multiple, $"Menu {index}"))
            .ToList();
        var older = new RoleMenuPageCursor(DateTime.UnixEpoch, menus[^1].Id, RoleMenuPageDirection.Older);
        var newer = new RoleMenuPageCursor(DateTime.UnixEpoch, menus[0].Id, RoleMenuPageDirection.Newer);

        var firstPage = RoleMenuComponents.BuildDeleteSelector(
            userId,
            new RoleMenuDeletionPage(menus, null, older),
            _ => "roles");
        var middlePage = RoleMenuComponents.BuildDeleteSelector(
            userId,
            new RoleMenuDeletionPage(menus, newer, older),
            _ => "roles");

        Assert.Equal(25, GetSelect(firstPage).Options.Count);
        Assert.Equal(["Next", "Cancel"], GetButtons(firstPage).Select(button => button.Label));
        Assert.Equal(
            ["Previous", "Next", "Cancel"],
            GetButtons(middlePage).Select(button => button.Label));
        Assert.Equal(
            RoleMenuCustomIds.DeletePage(userId, newer),
            GetButtons(middlePage)[0].CustomId);
        Assert.Equal(
            RoleMenuCustomIds.DeletePage(userId, older),
            GetButtons(middlePage)[1].CustomId);
        Assert.All(
            GetButtons(middlePage),
            button => Assert.True(button.CustomId.Length <= ComponentBuilder.MaxCustomIdLength));
    }

    [Fact]
    public void BuildDeleteConfirmationComponents_CurrentPanelOffersDeleteCancelAndViewLink()
    {
        const ulong userId = 123UL;
        var settings = CreateRealisticSettings("Game Roles", "234567890123456789");

        var buttons = GetButtons(RoleMenuComponents.BuildDeleteConfirmationComponents(
            userId,
            settings,
            RoleMenuPanelState.Current));

        var delete = Assert.Single(
            buttons,
            button => button.CustomId == RoleMenuCustomIds.DeleteConfirm(
                userId,
                settings.Id,
                settings.UpdatedAtUtc.Ticks));
        Assert.Equal("Delete menu", delete.Label);
        Assert.Equal(ButtonStyle.Danger, delete.Style);
        var cancel = Assert.Single(
            buttons,
            button => button.CustomId == RoleMenuCustomIds.DeleteCancel(userId));
        Assert.Equal("Cancel", cancel.Label);
        Assert.Equal(ButtonStyle.Secondary, cancel.Style);
        var view = Assert.Single(buttons, button => button.Style == ButtonStyle.Link);
        Assert.Equal("View menu", view.Label);
        Assert.Equal(
            "https://discord.com/channels/134567890123456789/234567890123456789/434567890123456789",
            view.Url);
    }

    [Theory]
    [InlineData((int)RoleMenuPanelState.MessageMissing)]
    [InlineData((int)RoleMenuPanelState.ChannelMissing)]
    [InlineData((int)RoleMenuPanelState.NotAPanel)]
    public void BuildDeleteConfirmationComponents_CleanupStatesOfferDeleteWithoutLink(
        int panelStateValue)
    {
        var panelState = (RoleMenuPanelState)panelStateValue;
        var buttons = GetButtons(RoleMenuComponents.BuildDeleteConfirmationComponents(
            123UL,
            CreateSettings(RoleMenuSelectionMode.Multiple),
            panelState));

        Assert.Equal(["Delete menu", "Cancel"], buttons.Select(button => button.Label));
        Assert.DoesNotContain(buttons, button => button.Style == ButtonStyle.Link);
    }

    [Theory]
    [InlineData((int)RoleMenuPanelState.Inaccessible)]
    [InlineData((int)RoleMenuPanelState.Unavailable)]
    public void BuildDeleteConfirmationComponents_UnverifiedPanelsOfferNoDelete(
        int panelStateValue)
    {
        var panelState = (RoleMenuPanelState)panelStateValue;
        var components = RoleMenuComponents.BuildDeleteConfirmationComponents(
            123UL,
            CreateSettings(RoleMenuSelectionMode.Multiple),
            panelState);

        Assert.Empty(components.Components);
    }

    [Fact]
    public void FormatDeleteConfirmationContent_DistinguishesMissingFromInaccessible()
    {
        var inaccessible = RoleMenuComponents.FormatDeleteConfirmationContent(
            RoleMenuPanelState.Inaccessible);
        var unavailable = RoleMenuComponents.FormatDeleteConfirmationContent(
            RoleMenuPanelState.Unavailable);

        Assert.Equal("Delete this role menu?", RoleMenuComponents.FormatDeleteConfirmationContent(
            RoleMenuPanelState.MessageMissing));
        Assert.Contains("can't open", inaccessible, StringComparison.Ordinal);
        Assert.Contains("couldn't check", unavailable, StringComparison.Ordinal);
        Assert.NotEqual(inaccessible, unavailable);
    }

    [Fact]
    public void BuildDeleteConfirmationEmbed_BoundsCorruptStoredTitleWithoutSplittingUnicode()
    {
        var settings = CreateSettings(
            RoleMenuSelectionMode.Multiple,
            new string('a', 98) + "😀" + "tail");

        var embed = RoleMenuComponents.BuildDeleteConfirmationEmbed(
            settings,
            RoleMenuPanelState.Current);

        Assert.NotNull(embed.Title);
        Assert.True(embed.Title.Length <= EmbedBuilder.MaxTitleLength);
        Assert.Equal(new string('a', 98) + "…", embed.Title);
        Assert.DoesNotContain(embed.Title, char.IsSurrogate);
        Assert.NotNull(embed.Description);
        Assert.True(embed.Description.Length <= EmbedBuilder.MaxDescriptionLength);
    }

    [Fact]
    public void BuildDeleteConfirmationEmbed_UsesFallbackForBlankStoredTitle()
    {
        var settings = CreateSettings(RoleMenuSelectionMode.Multiple, " ");

        var embed = RoleMenuComponents.BuildDeleteConfirmationEmbed(
            settings,
            RoleMenuPanelState.Current);

        Assert.Equal("Untitled role menu", embed.Title);
    }

    [Fact]
    public void BuildDeleteConfirmationEmbed_NamesChannelRolesModeAndCreationTime()
    {
        var settings = CreateSettings(RoleMenuSelectionMode.Multiple, "Game Roles");
        settings.CreatedAtUtc = new DateTime(2026, 10, 10, 9, 15, 0, DateTimeKind.Utc);

        var embed = RoleMenuComponents.BuildDeleteConfirmationEmbed(
            settings,
            RoleMenuPanelState.Current,
            new DateTime(2026, 10, 10, 11, 15, 0, DateTimeKind.Utc));

        Assert.Equal("Game Roles", embed.Title);
        Assert.Equal(
            "In <#2>\n2 roles members can choose • Members can choose any number\n" +
            "Created 2 hours ago\n\nMembers keep the roles they already have.",
            embed.Description);
    }

    [Theory]
    [InlineData((int)RoleMenuPanelState.MessageMissing, "The menu's message was deleted.")]
    [InlineData((int)RoleMenuPanelState.ChannelMissing, "The menu's channel was deleted.")]
    [InlineData(
        (int)RoleMenuPanelState.NotAPanel,
        "The saved message isn't this role menu anymore, so I'll leave it alone.")]
    public void BuildDeleteConfirmationEmbed_ExplainsCleanupStates(
        int panelStateValue,
        string expectedNote)
    {
        var panelState = (RoleMenuPanelState)panelStateValue;
        var embed = RoleMenuComponents.BuildDeleteConfirmationEmbed(
            CreateSettings(RoleMenuSelectionMode.Exclusive),
            panelState);

        Assert.Contains(expectedNote, embed.Description, StringComparison.Ordinal);
        Assert.Contains("Members can choose one", embed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDeleteConfirmationEmbed_OmitsUnreadableChannel()
    {
        var settings = new RoleMenuSettings(
            ObjectId.GenerateNewId(),
            "1",
            "not-a-channel",
            "3",
            "Games",
            string.Empty,
            ["10"],
            RoleMenuSelectionMode.Exclusive);

        var embed = RoleMenuComponents.BuildDeleteConfirmationEmbed(
            settings,
            RoleMenuPanelState.NotAPanel);

        Assert.StartsWith("1 role members can choose", embed.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("<#", embed.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)RoleMenuPanelState.Current)]
    [InlineData((int)RoleMenuPanelState.MessageMissing)]
    [InlineData((int)RoleMenuPanelState.ChannelMissing)]
    [InlineData((int)RoleMenuPanelState.NotAPanel)]
    [InlineData((int)RoleMenuPanelState.Inaccessible)]
    [InlineData((int)RoleMenuPanelState.Unavailable)]
    public void DeleteConfirmation_VisibleTextNeverShowsRawIds(int panelStateValue)
    {
        var panelState = (RoleMenuPanelState)panelStateValue;
        var settings = CreateRealisticSettings("Game Roles", "234567890123456789");

        var embed = RoleMenuComponents.BuildDeleteConfirmationEmbed(settings, panelState);
        var buttons = GetButtons(RoleMenuComponents.BuildDeleteConfirmationComponents(
            123UL,
            settings,
            panelState));

        AssertNoRawIds(
            RoleMenuComponents.FormatDeleteConfirmationContent(panelState),
            embed.Title,
            embed.Description);
        Assert.All(buttons, button => AssertNoRawIds(button.Label));
    }

    [Theory]
    [InlineData(0, "No roles members can choose")]
    [InlineData(1, "1 role members can choose")]
    [InlineData(2, "2 roles members can choose")]
    [InlineData(25, "25 roles members can choose")]
    public void FormatChoosableRoleCount_UsesCorrectSingularAndPlural(int count, string expected)
    {
        Assert.Equal(expected, RoleMenuComponents.FormatChoosableRoleCount(count));
    }

    [Fact]
    public void DeleteBuilders_NullArgumentsThrow()
    {
        Assert.Equal(
            "page",
            Assert.Throws<ArgumentNullException>(
                () => RoleMenuComponents.BuildDeleteSelector(123UL, null!, _ => null)).ParamName);
        Assert.Equal(
            "settings",
            Assert.Throws<ArgumentNullException>(
                () => RoleMenuComponents.BuildDeleteConfirmationEmbed(
                    null!,
                    RoleMenuPanelState.Current)).ParamName);
        Assert.Equal(
            "settings",
            Assert.Throws<ArgumentNullException>(
                () => RoleMenuComponents.BuildDeleteConfirmationComponents(
                    123UL,
                    null!,
                    RoleMenuPanelState.Current)).ParamName);
    }

    [Fact]
    public void BuildViewMenuLink_IsALabelledLink()
    {
        var button = Assert.Single(GetButtons(RoleMenuComponents.BuildViewMenuLink(
            "https://discord.com/channels/1/2/3")));

        Assert.Equal("View menu", button.Label);
        Assert.Equal(ButtonStyle.Link, button.Style);
        Assert.Equal("https://discord.com/channels/1/2/3", button.Url);
    }

    [Fact]
    public void HasManageButton_DetectsOnlyMatchingManageAction()
    {
        var menuId = ObjectId.GenerateNewId();
        var message = CreateMessage(RoleMenuComponents.BuildPublicComponents(menuId));

        Assert.True(RoleMenuComponents.HasManageButton(message, menuId));
        Assert.False(RoleMenuComponents.HasManageButton(
            message,
            ObjectId.GenerateNewId()));
        Assert.False(RoleMenuComponents.HasManageButton(
            CreateMessage(MessageComponent.Empty),
            menuId));
    }

    [Fact]
    public void HasManageButton_NullMessageThrows()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            RoleMenuComponents.HasManageButton(null!, ObjectId.GenerateNewId()));

        Assert.Equal("message", exception.ParamName);
    }

    private static SelectMenuComponent GetSelect(MessageComponent components)
        => Assert.IsType<SelectMenuComponent>(
            Assert.Single(
                Assert.IsType<ActionRowComponent>(components.Components.First())
                    .Components));

    private static ButtonComponent[] GetButtons(MessageComponent components)
        => [.. components.Components
            .Select(component => Assert.IsType<ActionRowComponent>(component))
            .SelectMany(row => row.Components)
            .OfType<ButtonComponent>()];

    private static IMessage CreateMessage(MessageComponent components)
    {
        var message = DispatchProxy.Create<IMessage, ComponentMessageProxy>();
        ((ComponentMessageProxy)message).Components = components.Components;
        return message;
    }

    private static void AssertClearButton(
        MessageComponent components,
        ObjectId menuId,
        ulong userId,
        ulong messageId)
    {
        var secondRow = Assert.IsType<ActionRowComponent>(components.Components.ElementAt(1));
        var button = Assert.IsType<ButtonComponent>(Assert.Single(secondRow.Components));
        Assert.Equal(RoleMenuCustomIds.Clear(menuId, userId, messageId), button.CustomId);
    }

    private static RoleMenuSettings CreateRealisticSettings(string title, string channelId)
        => new(
            ObjectId.GenerateNewId(),
            "134567890123456789",
            channelId,
            "434567890123456789",
            title,
            string.Empty,
            ["534567890123456789", "634567890123456789"],
            RoleMenuSelectionMode.Multiple);

    /// <summary>
    /// Visible text must not show database IDs, Discord snowflakes or raw message links. Channel
    /// mentions are allowed because Discord renders them as channel names.
    /// </summary>
    private static void AssertNoRawIds(params string?[] visibleTexts)
    {
        foreach (var text in visibleTexts)
        {
            if (text is null)
            {
                continue;
            }

            var withoutMentions = Regex.Replace(text, "<#[0-9]+>", "#channel");
            Assert.DoesNotMatch("[0-9a-fA-F]{24}", withoutMentions);
            Assert.DoesNotMatch("[0-9]{15,}", withoutMentions);
            Assert.DoesNotContain("discord.com/channels", withoutMentions, StringComparison.Ordinal);
        }
    }

    private static RoleMenuSettings CreateSettings(
        RoleMenuSelectionMode selectionMode,
        string title = "Games")
        => new(
            ObjectId.GenerateNewId(),
            "1",
            "2",
            "3",
            title,
            string.Empty,
            ["10", "20"],
            selectionMode);

    private static ParsedRoleMenuSettings CreateParsedSettings()
        => new(1UL, 2UL, 3UL, [10UL, 20UL]);

    private static RoleMenuDraft CreateDraft(
        RoleMenuSelectionMode selectionMode,
        string description = "Choose roles")
        => new(
            Guid.NewGuid(),
            ObjectId.GenerateNewId(),
            1UL,
            123UL,
            2UL,
            "Games",
            description,
            [10UL, 20UL],
            selectionMode,
            DateTimeOffset.UtcNow.AddMinutes(10));

    private static IReadOnlyCollection<RoleMenuRoleSnapshot> CreateRoles()
        =>
        [
            new RoleMenuRoleSnapshot(10UL, "Alpha", false, false, 1),
            new RoleMenuRoleSnapshot(20UL, "Beta", false, false, 2),
            new RoleMenuRoleSnapshot(99UL, "Not configured", false, false, 3)
        ];

    public class ComponentMessageProxy : DispatchProxy
    {
        public IReadOnlyCollection<IMessageComponent> Components { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_Components"
                ? Components
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
