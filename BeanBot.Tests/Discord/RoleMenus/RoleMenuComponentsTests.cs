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

    private static SelectMenuComponent GetSelect(MessageComponent components)
        => Assert.IsType<SelectMenuComponent>(
            Assert.Single(
                Assert.IsType<ActionRowComponent>(components.Components.First())
                    .Components));

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

    private static IReadOnlyCollection<RoleMenuRoleSnapshot> CreateRoles()
        =>
        [
            new RoleMenuRoleSnapshot(10UL, "Alpha", false, false, 1),
            new RoleMenuRoleSnapshot(20UL, "Beta", false, false, 2),
            new RoleMenuRoleSnapshot(99UL, "Not configured", false, false, 3)
        ];
}
