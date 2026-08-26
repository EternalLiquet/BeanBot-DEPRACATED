using BeanBot.Discord.Commands;
using Discord;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class MemeModuleTests
{
    [Fact]
    public void NormalizeSuccTarget_WithNoArguments_DefaultsToAuthor()
    {
        var result = MemeModule.NormalizeSuccTarget(Array.Empty<string>(), "@author");

        Assert.Equal("@author", result);
    }

    [Theory]
    [InlineData("Bean Bot")]
    [InlineData("BEAN BOT please")]
    [InlineData("<@!630470467261693982>")]
    public void NormalizeSuccTarget_TargetingBot_RedirectsToAuthor(string target)
    {
        var result = MemeModule.NormalizeSuccTarget(target.Split(' '), "@author");

        Assert.Equal("@author", result);
    }

    [Fact]
    public void NormalizeSuccTarget_RemovesDuplicateCommandWordWithoutMutatingInput()
    {
        var input = new[] { "succ", "@friend" };

        var result = MemeModule.NormalizeSuccTarget(input, "@author");

        Assert.Equal("@friend", result);
        Assert.Equal(new[] { "succ", "@friend" }, input);
    }

    [Theory]
    [InlineData("@everyone")]
    [InlineData("@here")]
    [InlineData("<@&123456789>")]
    [InlineData("<@987654321>")]
    [InlineData("plain text")]
    public void CreateMentionSafeReply_PreservesContentAndUsesDiscordNoMentionPolicy(string content)
    {
        var reply = MemeModule.CreateMentionSafeReply(content);

        Assert.Equal(content, reply.Content);
        AssertMentionsDisabled(reply.AllowedMentions);
    }

    [Fact]
    public void CreateMentionSafeReply_SuccRoleTarget_PreservesVisibleTargetWithoutNotificationPolicy()
    {
        var target = MemeModule.NormalizeSuccTarget(new[] { "<@&123456789>" }, "<@987654321>");
        var content = $"*succ succ succ* lol you're gay {target}";

        var reply = MemeModule.CreateMentionSafeReply(content);

        Assert.Equal("*succ succ succ* lol you're gay <@&123456789>", reply.Content);
        AssertMentionsDisabled(reply.AllowedMentions);
    }

    [Fact]
    public void CreateMentionSafeReply_FortuneQuestion_PreservesQuotedMentionsWithoutNotificationPolicy()
    {
        const string question = "will @everyone and <@987654321> have a good day?";
        var content = $"> {question} \nYeehaw";

        var reply = MemeModule.CreateMentionSafeReply(content);

        Assert.Equal("> will @everyone and <@987654321> have a good day? \nYeehaw", reply.Content);
        AssertMentionsDisabled(reply.AllowedMentions);
    }

    [Fact]
    public void GetSafeMediaSource_RemovesQueryAndUserInfo()
    {
        var source = MemeModule.GetSafeMediaSource(
            new Uri("https://user:secret@example.test/images/toes.png?signature=top-secret#fragment"));

        Assert.Equal("https://example.test/images/toes.png", source);
    }

    private static void AssertMentionsDisabled(AllowedMentions allowedMentions)
    {
        var parsedTypes = allowedMentions.AllowedTypes ?? AllowedMentionTypes.None;
        var notificationTypes = AllowedMentionTypes.Users | AllowedMentionTypes.Roles | AllowedMentionTypes.Everyone;

        Assert.Equal(AllowedMentionTypes.None, parsedTypes & notificationTypes);
        Assert.True(allowedMentions.UserIds is null || allowedMentions.UserIds.Count == 0);
        Assert.True(allowedMentions.RoleIds is null || allowedMentions.RoleIds.Count == 0);
    }
}
