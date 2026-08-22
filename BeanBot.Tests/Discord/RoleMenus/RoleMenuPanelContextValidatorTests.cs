using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuPanelContextValidatorTests
{
    private static readonly ParsedRoleMenuSettings Settings = new(
        1UL,
        2UL,
        3UL,
        [4UL]);

    [Fact]
    public void Validate_ExactPersistedPanel_IsAccepted()
    {
        var issue = RoleMenuPanelContextValidator.Validate(
            Settings,
            1UL,
            2UL,
            3UL,
            10UL,
            10UL,
            true);

        Assert.Equal(RoleMenuPanelContextIssue.None, issue);
    }

    [Theory]
    [InlineData(99UL, 2UL, 3UL, 10UL, 10UL, true, RoleMenuPanelContextIssue.GuildMismatch)]
    [InlineData(1UL, 99UL, 3UL, 10UL, 10UL, true, RoleMenuPanelContextIssue.ChannelMismatch)]
    [InlineData(1UL, 2UL, 99UL, 10UL, 10UL, true, RoleMenuPanelContextIssue.MessageMismatch)]
    [InlineData(1UL, 2UL, 3UL, 99UL, 10UL, true, RoleMenuPanelContextIssue.UnexpectedAuthor)]
    [InlineData(1UL, 2UL, 3UL, 10UL, 10UL, false, RoleMenuPanelContextIssue.MissingManageButton)]
    public void Validate_StaleOrForgedPanel_IsRejected(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong authorId,
        ulong botId,
        bool hasManageButton,
        RoleMenuPanelContextIssue expectedIssue)
    {
        var issue = RoleMenuPanelContextValidator.Validate(
            Settings,
            guildId,
            channelId,
            messageId,
            authorId,
            botId,
            hasManageButton);

        Assert.Equal(expectedIssue, issue);
    }
}
