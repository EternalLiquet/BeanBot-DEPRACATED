using BeanBot.Discord.RoleMenus;
using Discord;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuPrivateControlValidatorTests
{
    private static readonly ObjectId MenuId = ObjectId.GenerateNewId();

    [Fact]
    public void Validate_ExactUserBoundEphemeralControl_IsAccepted()
    {
        var issue = Validate(out var binding);

        Assert.Equal(RoleMenuPrivateControlIssue.None, issue);
        Assert.Equal(MenuId, binding.MenuId);
        Assert.Equal(20UL, binding.PanelMessageId);
    }

    [Fact]
    public void Validate_ControlBoundToAnotherUser_IsRejected()
    {
        var issue = Validate(out _, userIdValue: "99");

        Assert.Equal(RoleMenuPrivateControlIssue.WrongUser, issue);
    }

    [Theory]
    [InlineData(false, ComponentType.SelectMenu, 30UL, true, (int)RoleMenuPrivateControlIssue.NotEphemeral)]
    [InlineData(true, ComponentType.Button, 30UL, true, (int)RoleMenuPrivateControlIssue.WrongComponentType)]
    [InlineData(true, ComponentType.SelectMenu, 99UL, true, (int)RoleMenuPrivateControlIssue.UnexpectedAuthor)]
    [InlineData(true, ComponentType.SelectMenu, 30UL, false, (int)RoleMenuPrivateControlIssue.MissingSourceComponent)]
    public void Validate_ForgedSourceContext_IsRejected(
        bool isEphemeral,
        ComponentType componentType,
        ulong messageAuthorId,
        bool hasSourceComponent,
        int expectedIssue)
    {
        var issue = Validate(
            out _,
            isEphemeral: isEphemeral,
            componentType: componentType,
            messageAuthorId: messageAuthorId,
            hasSourceComponent: hasSourceComponent);

        Assert.Equal((RoleMenuPrivateControlIssue)expectedIssue, issue);
    }

    [Theory]
    [InlineData("invalid", "10", "20", (int)RoleMenuPrivateControlIssue.InvalidMenuId)]
    [InlineData(null, "invalid", "20", (int)RoleMenuPrivateControlIssue.InvalidUserId)]
    [InlineData(null, "10", "invalid", (int)RoleMenuPrivateControlIssue.InvalidPanelMessageId)]
    public void Validate_MalformedIdentifiers_AreRejected(
        string? menuIdValue,
        string userIdValue,
        string panelMessageIdValue,
        int expectedIssue)
    {
        var issue = Validate(
            out _,
            menuIdValue: menuIdValue,
            userIdValue: userIdValue,
            panelMessageIdValue: panelMessageIdValue);

        Assert.Equal((RoleMenuPrivateControlIssue)expectedIssue, issue);
    }

    private static RoleMenuPrivateControlIssue Validate(
        out RoleMenuPrivateControlBinding binding,
        string? menuIdValue = null,
        string userIdValue = "10",
        string panelMessageIdValue = "20",
        bool isEphemeral = true,
        ComponentType componentType = ComponentType.SelectMenu,
        ulong messageAuthorId = 30UL,
        bool hasSourceComponent = true)
        => RoleMenuPrivateControlValidator.Validate(
            menuIdValue ?? MenuId.ToString(),
            userIdValue,
            panelMessageIdValue,
            10UL,
            isEphemeral,
            componentType,
            ComponentType.SelectMenu,
            messageAuthorId,
            30UL,
            hasSourceComponent,
            out binding);
}
