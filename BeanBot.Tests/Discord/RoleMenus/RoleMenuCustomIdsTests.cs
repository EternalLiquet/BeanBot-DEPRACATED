using BeanBot.Discord.RoleMenus;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuCustomIdsTests
{
    [Fact]
    public void Builders_StayWithinDiscordLimitAndRoundTripBoundValues()
    {
        var menuId = ObjectId.GenerateNewId();
        const ulong maximumSnowflake = ulong.MaxValue;

        var save = RoleMenuCustomIds.Save(menuId, maximumSnowflake, maximumSnowflake);
        var clear = RoleMenuCustomIds.Clear(menuId, maximumSnowflake, maximumSnowflake);

        Assert.True(save.Length <= 100);
        Assert.True(clear.Length <= 100);
        Assert.Contains(menuId.ToString(), save, StringComparison.Ordinal);
        Assert.Contains(maximumSnowflake.ToString(), save, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("000000000000000000000000")]
    [InlineData("not-an-object-id")]
    [InlineData("507f1f77bcf86cd79943901")]
    [InlineData("507f1f77bcf86cd7994390111")]
    public void TryParseMenuId_RejectsMalformedAndEmptyValues(string value)
    {
        Assert.False(RoleMenuCustomIds.TryParseMenuId(value, out _));
    }

    [Fact]
    public void TryParseMenuId_RejectsNonCanonicalCase()
    {
        const string canonical = "507f1f77bcf86cd799439011";

        Assert.True(RoleMenuCustomIds.TryParseMenuId(canonical, out _));
        Assert.False(RoleMenuCustomIds.TryParseMenuId(canonical.ToUpperInvariant(), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData(" 42")]
    [InlineData("42 ")]
    [InlineData("1.0")]
    public void TryParseSnowflake_RejectsNonCanonicalValues(string value)
    {
        Assert.False(RoleMenuCustomIds.TryParseSnowflake(value, out _));
    }

    [Fact]
    public void TryParseDraftId_RequiresCompactNonEmptyGuid()
    {
        var draftId = Guid.NewGuid();

        Assert.True(RoleMenuCustomIds.TryParseDraftId(draftId.ToString("N"), out var parsed));
        Assert.Equal(draftId, parsed);
        Assert.False(RoleMenuCustomIds.TryParseDraftId(draftId.ToString("D"), out _));
        Assert.False(RoleMenuCustomIds.TryParseDraftId(Guid.Empty.ToString("N"), out _));
    }
}
