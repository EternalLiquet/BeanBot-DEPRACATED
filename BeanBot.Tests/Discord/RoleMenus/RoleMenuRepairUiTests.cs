using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Discord;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuRepairUiTests
{
    [Theory]
    [InlineData((int)RoleMenuRepairPanelIssue.ChannelMissing, "old channel is gone")]
    [InlineData((int)RoleMenuRepairPanelIssue.MessageMissing, "menu message is gone")]
    public void Confirmation_PreviewsTheMissingPanelAndReplacement(
        int issue,
        string expectedReason)
    {
        var settings = new RoleMenuSettings(
            ObjectId.GenerateNewId(), "10", "20", "30", "Game Roles", "Choose games",
            ["100"], RoleMenuSelectionMode.Exclusive);

        var embed = RoleMenuRepairUi.BuildConfirmationEmbed(
            settings,
            [new RoleMenuRoleSnapshot(100UL, "Gamer", false, false, 1)],
            40UL,
            (RoleMenuRepairPanelIssue)issue);

        Assert.Contains(expectedReason, embed.Description, StringComparison.Ordinal);
        Assert.Contains("post a replacement", embed.Description, StringComparison.Ordinal);
        Assert.Contains(embed.Fields, field => field.Name == "Menu" && field.Value == "Game Roles");
        Assert.Contains(embed.Fields, field => field.Name == "Roles" && field.Value == "<@&100>");
        Assert.Contains(embed.Fields, field => field.Name == "Mode" && field.Value == "Single selection");
        Assert.Contains(embed.Fields, field => field.Name == "Target" && field.Value == "<#40>");
    }

    [Fact]
    public void ConfirmationButtons_BindRepairToAdministratorMenuAndChannel()
    {
        var menuId = ObjectId.GenerateNewId();

        var components = RoleMenuRepairUi.BuildConfirmationComponents(70UL, menuId, 40UL);

        var row = Assert.IsType<ActionRowComponent>(Assert.Single(components.Components));
        var buttons = row.Components.Select(component => Assert.IsType<ButtonComponent>(component)).ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.Equal("Repair", buttons[0].Label);
        Assert.Equal(RoleMenuRepairUi.Confirm(70UL, menuId, 40UL), buttons[0].CustomId);
        Assert.Equal("Cancel", buttons[1].Label);
        Assert.Equal(RoleMenuRepairUi.Cancel(70UL), buttons[1].CustomId);
    }
}
