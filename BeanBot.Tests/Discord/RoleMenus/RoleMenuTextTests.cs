using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuTextTests
{
    [Fact]
    public void TruncateWithEllipsis_DoesNotSplitSurrogatePair()
    {
        var value = new string('a', 38) + "😀" + "tail";

        var result = RoleMenuText.TruncateWithEllipsis(value, 40);

        Assert.Equal(new string('a', 38) + "…", result);
        Assert.DoesNotContain(result, char.IsSurrogate);
    }

    [Fact]
    public void TruncateWithEllipsis_PreservesValueWithinLimit()
    {
        const string value = "Games 😀";

        Assert.Same(value, RoleMenuText.TruncateWithEllipsis(value, value.Length));
    }

    [Fact]
    public void TruncateWithEllipsis_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => RoleMenuText.TruncateWithEllipsis(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RoleMenuText.TruncateWithEllipsis("value", 0));
    }
}
