using System.Globalization;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuEditPickerTests
{
    [Fact]
    public void EditPicker_UsesBoundedPageWithOlderAndNewerNavigation()
    {
        var menus = Enumerable.Range(1, 25).Select(index => CreateSettings(index)).ToList();
        var previous = RoleMenuDeletionTargets.CreateCursor(menus[0], RoleMenuPageDirection.Newer);
        var next = RoleMenuDeletionTargets.CreateCursor(menus[^1], RoleMenuPageDirection.Older);
        var page = new RoleMenuDeletionPage(menus, previous, next);

        var component = RoleMenuComponents.BuildEditSelector(
            42UL, page, _ => "games", new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));
        var components = component.Components.OfType<ActionRowComponent>()
            .SelectMany(row => row.Components).ToList();
        var select = Assert.Single(components.OfType<SelectMenuComponent>());
        Assert.Equal(RoleMenuCustomIds.EditSelect(42UL), select.CustomId);
        Assert.Equal(25, select.Options.Count);
        Assert.Equal(menus[^1].Id.ToString(), select.Options.Last().Value);
        Assert.All(select.Options, option => Assert.Contains("games", option.Description));
        Assert.Contains(components.OfType<ButtonComponent>(),
            button => button.CustomId == RoleMenuCustomIds.EditPage(42UL, previous));
        Assert.Contains(components.OfType<ButtonComponent>(),
            button => button.CustomId == RoleMenuCustomIds.EditPage(42UL, next));
    }

    [Fact]
    public void EditSummary_DoesNotExposeMenuIdInFooter()
    {
        var settings = CreateSettings(1);
        var registry = new RoleMenuEditDraftRegistry();
        Assert.Equal(RoleMenuEditDraftCreateStatus.Created, registry.Create(
            settings.Id, 1UL, 42UL, settings.Title, settings.Description,
            [10UL], settings.SelectionMode, out var draft,
            RoleMenuEditSnapshot.From(settings)));

        var embed = RoleMenuComponents.BuildEditSummaryEmbed(draft!);
        Assert.DoesNotContain(settings.Id.ToString(), embed.Footer.GetValueOrDefault().Text);
        Assert.DoesNotContain("ID", embed.Footer.GetValueOrDefault().Text, StringComparison.Ordinal);
    }

    private static RoleMenuSettings CreateSettings(int index)
        => new(ObjectId.GenerateNewId(), "1", "2", index.ToString(CultureInfo.InvariantCulture),
            $"Menu {index}", string.Empty, ["10"], RoleMenuSelectionMode.Multiple)
        {
            CreatedAtUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc)
        };
}
