using BeanBot.Modules;
using Xunit;

namespace BeanBot.Tests.Modules;

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
}
