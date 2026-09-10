using System.Reflection;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
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
        Assert.Equal("Manage Roles", button.Label);
        Assert.Equal(RoleMenuCustomIds.Manage(menuId), button.CustomId);
        Assert.Equal(ButtonStyle.Primary, button.Style);
    }

    [Fact]
    public void BuildPublicEmbed_IncludesDeletionLookupIdAndMode()
    {
        var menuId = ObjectId.GenerateNewId();

        var embed = RoleMenuComponents.BuildPublicEmbed(
            menuId,
            "Games",
            string.Empty,
            RoleMenuSelectionMode.Exclusive);

        Assert.True(embed.Footer.HasValue);
        var footer = embed.Footer.GetValueOrDefault();
        Assert.Contains(menuId.ToString(), footer.Text, StringComparison.Ordinal);
        Assert.Contains("Choose one role", footer.Text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(embed.Description));
    }

    [Fact]
    public void BuildPublicEmbed_MultipleModePreservesNonblankDescription()
    {
        var menuId = ObjectId.GenerateNewId();

        var embed = RoleMenuComponents.BuildPublicEmbed(
            menuId,
            "Games",
            "Choose the games you play.",
            RoleMenuSelectionMode.Multiple);

        Assert.Equal("Games", embed.Title);
        Assert.Equal("Choose the games you play.", embed.Description);
        Assert.True(embed.Footer.HasValue);
        var footer = embed.Footer.GetValueOrDefault();
        Assert.Contains("Choose any combination", footer.Text, StringComparison.Ordinal);
        Assert.Contains(menuId.ToString(), footer.Text, StringComparison.Ordinal);
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
            "Multiple selection",
            Assert.Single(embed.Fields, field => field.Name == "Mode").Value);
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
            "Choose the roles you want. You can update this at any time.",
            embed.Description);
        Assert.Equal(
            "No valid roles remain.",
            Assert.Single(embed.Fields, field => field.Name == "Roles").Value);
        Assert.Equal(
            "Single selection",
            Assert.Single(embed.Fields, field => field.Name == "Mode").Value);
        Assert.Equal("Preview • Not published", embed.Footer.GetValueOrDefault().Text);
    }

    [Fact]
    public void BuildPreviewComponents_ContainsPublishAndCancelActions()
    {
        var draftId = Guid.NewGuid();

        var buttons = GetButtons(RoleMenuComponents.BuildPreviewComponents(draftId));

        var publish = Assert.Single(
            buttons,
            button => button.CustomId == RoleMenuCustomIds.Publish(draftId));
        Assert.Equal("Publish", publish.Label);
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
    public void BuildDeleteSelector_RendersStaleAndDatedMenus()
    {
        const ulong userId = 123UL;
        var stale = CreateSettings(RoleMenuSelectionMode.Multiple, " ");
        var normal = CreateSettings(RoleMenuSelectionMode.Exclusive, "Music");
        normal.CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        var select = GetSelect(RoleMenuComponents.BuildDeleteSelector(
            userId,
            [stale, normal]));

        Assert.Equal(RoleMenuCustomIds.DeleteSelect(userId), select.CustomId);
        Assert.Equal(1, select.MinValues);
        Assert.Equal(1, select.MaxValues);
        var staleOption = Assert.Single(
            select.Options,
            option => option.Value == stale.Id.ToString());
        Assert.Equal("Untitled or stale role menu", staleOption.Label);
        Assert.Equal(
            "Multiple selection • Unknown creation date",
            staleOption.Description);
        var normalOption = Assert.Single(
            select.Options,
            option => option.Value == normal.Id.ToString());
        Assert.Equal("Music", normalOption.Label);
        Assert.Equal("Single selection • 2026-08-20", normalOption.Description);
    }

    [Fact]
    public void BuildDeleteConfirmationComponents_ContainsDeleteAndCancelActions()
    {
        const ulong userId = 123UL;
        var menuId = ObjectId.GenerateNewId();

        var buttons = GetButtons(
            RoleMenuComponents.BuildDeleteConfirmationComponents(userId, menuId));

        var delete = Assert.Single(
            buttons,
            button => button.CustomId == RoleMenuCustomIds.DeleteConfirm(userId, menuId));
        Assert.Equal("Delete", delete.Label);
        Assert.Equal(ButtonStyle.Danger, delete.Style);
        var cancel = Assert.Single(
            buttons,
            button => button.CustomId == RoleMenuCustomIds.DeleteCancel(userId));
        Assert.Equal("Cancel", cancel.Label);
        Assert.Equal(ButtonStyle.Secondary, cancel.Style);
    }

    [Fact]
    public void BuildDeleteConfirmationEmbed_BoundsCorruptStoredTitleWithoutSplittingUnicode()
    {
        var settings = CreateSettings(
            RoleMenuSelectionMode.Multiple,
            new string('a', 98) + "😀" + "tail");

        var embed = RoleMenuComponents.BuildDeleteConfirmationEmbed(settings);

        Assert.NotNull(embed.Description);
        Assert.True(embed.Description.Length <= EmbedBuilder.MaxDescriptionLength);
        Assert.Contains(new string('a', 98) + "…", embed.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(embed.Description, char.IsSurrogate);
    }

    [Fact]
    public void BuildDeleteConfirmationEmbed_UsesFallbackForBlankStoredTitle()
    {
        var settings = CreateSettings(RoleMenuSelectionMode.Multiple, " ");

        var embed = RoleMenuComponents.BuildDeleteConfirmationEmbed(settings);

        Assert.Contains("Untitled or stale role menu", embed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteBuilders_NullSettingsThrow()
    {
        Assert.Equal(
            "settings",
            Assert.Throws<ArgumentNullException>(
                () => RoleMenuComponents.BuildDeleteSelector(123UL, null!)).ParamName);
        Assert.Equal(
            "settings",
            Assert.Throws<ArgumentNullException>(
                () => RoleMenuComponents.BuildDeleteConfirmationEmbed(null!)).ParamName);
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
    {
        var row = Assert.IsType<ActionRowComponent>(Assert.Single(components.Components));
        return [.. row.Components.OfType<ButtonComponent>()];
    }

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
