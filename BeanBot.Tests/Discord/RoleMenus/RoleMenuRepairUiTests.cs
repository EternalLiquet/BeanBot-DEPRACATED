using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
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

        var settings = new RoleMenuSettings(
            menuId, "10", "20", "30", "Game Roles", "Choose games",
            ["100"], RoleMenuSelectionMode.Exclusive);
        var fingerprint = RoleMenuRepairWorkflow.GetPreviewFingerprint(settings);
        var components = RoleMenuRepairUi.BuildConfirmationComponents(70UL, settings, 40UL);

        var row = Assert.IsType<ActionRowComponent>(Assert.Single(components.Components));
        var buttons = row.Components.Select(component => Assert.IsType<ButtonComponent>(component)).ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.Equal("Repair", buttons[0].Label);
        Assert.Equal(RoleMenuRepairUi.Confirm(70UL, menuId, 40UL, fingerprint), buttons[0].CustomId);
        Assert.True(buttons[0].CustomId.Length <= ComponentBuilder.MaxCustomIdLength);
        Assert.Equal("Cancel", buttons[1].Label);
        Assert.Equal(RoleMenuRepairUi.Cancel(70UL), buttons[1].CustomId);
    }

    [Fact]
    public void Selector_ListsMenusWithoutShowingRawIdsAndCarriesTargetAcrossPages()
    {
        var menu = new RoleMenuSettings(
            ObjectId.GenerateNewId(), "10", "20", "30", "Game Roles", "Choose games",
            ["100"], RoleMenuSelectionMode.Multiple);
        var cursor = new RoleMenuPageCursor(
            new DateTime(2026, 10, 10, 1, 0, 0, DateTimeKind.Utc),
            menu.Id,
            RoleMenuPageDirection.Older);
        var page = new RoleMenuDeletionPage([menu], null, cursor);

        var components = RoleMenuRepairUi.BuildSelector(70UL, 40UL, page, _ => "games");

        var rows = components.Components.OfType<ActionRowComponent>().ToArray();
        var selector = Assert.Single(rows.SelectMany(row => row.Components).OfType<SelectMenuComponent>());
        Assert.Equal(RoleMenuRepairUi.Select(70UL, 40UL), selector.CustomId);
        var option = Assert.Single(selector.Options);
        Assert.Equal(menu.Id.ToString(), option.Value);
        Assert.Equal("Game Roles", option.Label);
        Assert.DoesNotContain(menu.Id.ToString(), option.Description, StringComparison.Ordinal);
        var next = Assert.Single(rows.SelectMany(row => row.Components)
            .OfType<ButtonComponent>(), button => button.Label == "Next");
        Assert.Equal(RoleMenuRepairUi.Page(70UL, 40UL, cursor), next.CustomId);
        Assert.True(next.CustomId.Length <= ComponentBuilder.MaxCustomIdLength);
    }

    [Fact]
    public void ConfirmationPreview_DoesNotShowRawMenuId()
    {
        var settings = new RoleMenuSettings(
            ObjectId.GenerateNewId(), "10", "20", "30", "Game Roles", "Choose games",
            ["100"], RoleMenuSelectionMode.Multiple);

        var embed = RoleMenuRepairUi.BuildConfirmationEmbed(
            settings, [new RoleMenuRoleSnapshot(100UL, "Gamer", false, false, 1)],
            40UL, RoleMenuRepairPanelIssue.MessageMissing);

        Assert.DoesNotContain(settings.Id.ToString(), embed.Footer?.Text ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedRepairFeedback_UsesLabeledViewMenuButton()
    {
        var feedback = RoleMenuAdminModule.FormatRepairPublication(
            new RoleMenuPublicationResult(RoleMenuPublicationStatus.Published, 30UL, []),
            10UL, 20UL);

        Assert.Equal("I repaired the role menu.", feedback.Content);
        Assert.DoesNotContain("https://", feedback.Content, StringComparison.Ordinal);
        var row = Assert.IsType<ActionRowComponent>(Assert.Single(feedback.Components!.Components));
        var button = Assert.IsType<ButtonComponent>(Assert.Single(row.Components));
        Assert.Equal("View menu", button.Label);
        Assert.Equal(ButtonStyle.Link, button.Style);
        Assert.Equal("https://discord.com/channels/10/20/30", button.Url);
    }

    [Fact]
    public void UncertainRepairFeedback_DoesNotOfferUnverifiedPanelLink()
    {
        var feedback = RoleMenuAdminModule.FormatRepairPublication(
            new RoleMenuPublicationResult(RoleMenuPublicationStatus.PanelOutcomeUnknown, null, []),
            10UL, 20UL);

        Assert.Contains("couldn't tell whether Discord posted", feedback.Content,
            StringComparison.Ordinal);
        Assert.Null(feedback.Components);
    }

    [Fact]
    public void RemovedSelectedMenu_InvitesPickingAgainWithoutRawIdAdvice()
    {
        var feedback = RoleMenuAdminModule.FormatRepairInspection(
            new RoleMenuRepairInspectionResult(RoleMenuRepairInspectionStatus.SettingsMissing));

        Assert.Contains("choose a menu", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("ID", feedback, StringComparison.Ordinal);
    }
}
