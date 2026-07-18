using BeanBot.Services;
using Xunit;

namespace BeanBot.Tests.Services;

public class EditMessageEventServicesTests
{
    [Theory]
    [InlineData("%8ball should I shower?")]
    [InlineData("%fortune should I shower?")]
    [InlineData("  %FORTUNE question")]
    [InlineData("succ 8ball question")]
    [InlineData("SuCc fortune question")]
    public void IsFortuneCommand_AcceptsSupportedPrefixesAndAliases(string content)
    {
        Assert.True(EditMessageEventServices.IsFortuneCommand(content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("%8ballistic question")]
    [InlineData("fortune question")]
    [InlineData("%pun")]
    [InlineData("succ fortune-cookie")]
    public void IsFortuneCommand_RejectsOtherMessages(string content)
    {
        Assert.False(EditMessageEventServices.IsFortuneCommand(content));
    }

    [Theory]
    [InlineData("<@123> 8ball question")]
    [InlineData("<@!123> fortune question")]
    public void IsFortuneCommand_AcceptsBeanBotMentionPrefix(string content)
    {
        Assert.True(EditMessageEventServices.IsFortuneCommand(content, 123));
    }

    [Fact]
    public void IsFortuneCommand_RejectsAnotherUsersMentionPrefix()
    {
        Assert.False(EditMessageEventServices.IsFortuneCommand("<@456> fortune question", 123));
    }
}
