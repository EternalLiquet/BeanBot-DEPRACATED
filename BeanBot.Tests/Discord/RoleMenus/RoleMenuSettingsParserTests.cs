using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuSettingsParserTests
{
    [Fact]
    public void TryParse_ValidSettings_ReturnsNumericIdentifiersInStoredOrder()
    {
        var settings = CreateSettings();

        var success = RoleMenuSettingsParser.TryParse(settings, out var parsed, out var issue);

        Assert.True(success);
        Assert.Equal(RoleMenuSettingsIssue.None, issue);
        var parsedSettings = Assert.IsType<ParsedRoleMenuSettings>(parsed);
        Assert.Equal(10UL, parsedSettings.GuildId);
        Assert.Equal(20UL, parsedSettings.ChannelId);
        Assert.Equal(30UL, parsedSettings.MessageId);
        Assert.Equal([40UL, 50UL], parsedSettings.RoleIds);
    }

    [Theory]
    [InlineData("", "20", "30", (int)RoleMenuSettingsIssue.InvalidGuild)]
    [InlineData("10", "invalid", "30", (int)RoleMenuSettingsIssue.InvalidChannel)]
    [InlineData("10", "20", "0", (int)RoleMenuSettingsIssue.InvalidMessage)]
    public void TryParse_InvalidLocation_IsRejected(
        string guildId,
        string channelId,
        string messageId,
        int expectedIssue)
    {
        var settings = CreateSettings(
            guildId: guildId,
            channelId: channelId,
            messageId: messageId);

        var success = RoleMenuSettingsParser.TryParse(settings, out _, out var issue);

        Assert.False(success);
        Assert.Equal((RoleMenuSettingsIssue)expectedIssue, issue);
    }

    [Fact]
    public void TryParse_DuplicateRoleId_IsRejected()
    {
        var settings = CreateSettings(roleIds: ["40", "40"]);

        var success = RoleMenuSettingsParser.TryParse(settings, out _, out var issue);

        Assert.False(success);
        Assert.Equal(RoleMenuSettingsIssue.DuplicateRoleId, issue);
    }

    [Fact]
    public void TryParse_InvalidSelectionMode_IsRejected()
    {
        var settings = CreateSettings(selectionMode: (RoleMenuSelectionMode)999);

        var success = RoleMenuSettingsParser.TryParse(settings, out _, out var issue);

        Assert.False(success);
        Assert.Equal(RoleMenuSettingsIssue.InvalidSelectionMode, issue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_BlankTitle_IsRejected(string title)
    {
        var settings = CreateSettings(title: title);

        var success = RoleMenuSettingsParser.TryParse(settings, out _, out var issue);

        Assert.False(success);
        Assert.Equal(RoleMenuSettingsIssue.InvalidTitle, issue);
    }

    [Fact]
    public void TryParse_OversizedDescription_IsRejected()
    {
        var settings = CreateSettings(
            description: new string(
                'x',
                RoleMenuConstants.MaximumDescriptionLength + 1));

        var success = RoleMenuSettingsParser.TryParse(settings, out _, out var issue);

        Assert.False(success);
        Assert.Equal(RoleMenuSettingsIssue.InvalidDescription, issue);
    }

    [Fact]
    public void TryParse_EmptyRoleList_IsRejected()
    {
        var settings = CreateSettings(roleIds: []);

        var success = RoleMenuSettingsParser.TryParse(settings, out _, out var issue);

        Assert.False(success);
        Assert.Equal(RoleMenuSettingsIssue.InvalidRoleCount, issue);
    }

    private static RoleMenuSettings CreateSettings(
        string guildId = "10",
        string channelId = "20",
        string messageId = "30",
        IReadOnlyCollection<string>? roleIds = null,
        RoleMenuSelectionMode selectionMode = RoleMenuSelectionMode.Multiple,
        string title = "Game roles",
        string description = "")
        => new(
            ObjectId.GenerateNewId(),
            guildId,
            channelId,
            messageId,
            title,
            description,
            roleIds ?? ["40", "50"],
            selectionMode);
}
