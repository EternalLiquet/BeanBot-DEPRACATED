using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuPublicationSettingsTests
{
    [Fact]
    public void CreateAndMatches_PreserveStableDraftIdentityAcrossRetries()
    {
        var menuId = ObjectId.GenerateNewId();
        var draft = CreateDraft(menuId);

        var first = RoleMenuPublicationSettings.Create(draft, 30UL);
        var retry = RoleMenuPublicationSettings.Create(draft, 30UL);

        Assert.Equal(menuId, first.Id);
        Assert.Equal(first.Id, retry.Id);
        Assert.True(RoleMenuPublicationSettings.Matches(first, draft, 30UL));
        Assert.False(RoleMenuPublicationSettings.Matches(first, draft, 31UL));
    }

    [Fact]
    public void Matches_RejectsMismatchedAllowlistOrMode()
    {
        var draft = CreateDraft(ObjectId.GenerateNewId());
        var mismatched = new RoleMenuSettings(
            draft.MenuId,
            "1",
            "2",
            "30",
            draft.Title,
            draft.Description,
            ["999"],
            RoleMenuSelectionMode.Exclusive);

        Assert.False(RoleMenuPublicationSettings.Matches(mismatched, draft, 30UL));
    }

    private static RoleMenuDraft CreateDraft(ObjectId menuId)
        => new(
            Guid.NewGuid(),
            menuId,
            1UL,
            10UL,
            2UL,
            "Games",
            "Choose games",
            [4UL, 5UL],
            RoleMenuSelectionMode.Multiple,
            DateTimeOffset.UtcNow.AddMinutes(10));
}
