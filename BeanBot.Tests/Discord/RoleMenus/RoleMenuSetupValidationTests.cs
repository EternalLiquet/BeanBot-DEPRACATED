using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuSetupValidationTests
{
    [Fact]
    public void TryParseSelectionMode_Null_ReturnsFalse()
    {
        var parsed = RoleMenuSetupValidation.TryParseSelectionMode(null, out _);

        Assert.False(parsed);
    }
}
