using System.Globalization;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Repositories;
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
        Assert.Contains(
            maximumSnowflake.ToString(CultureInfo.InvariantCulture),
            save,
            StringComparison.Ordinal);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DeletePage_RoundTripsCursorWithinDiscordLimit(int directionValue)
    {
        var cursor = new RoleMenuPageCursor(
            DateTime.MaxValue,
            ObjectId.GenerateNewId(),
            (RoleMenuPageDirection)directionValue);

        var customId = RoleMenuCustomIds.DeletePage(ulong.MaxValue, cursor);
        var parts = customId.Split(':');

        Assert.True(customId.Length <= 100);
        Assert.Equal(6, parts.Length);
        Assert.Equal(ulong.MaxValue.ToString(CultureInfo.InvariantCulture), parts[2]);
        Assert.True(RoleMenuCustomIds.TryParsePageCursor(parts[3], parts[4], parts[5], out var parsed));
        Assert.Equal(cursor, parsed);
    }

    [Theory]
    [InlineData("x", "0", "507f1f77bcf86cd799439011")]
    [InlineData("o", "-1", "507f1f77bcf86cd799439011")]
    [InlineData("o", "01", "507f1f77bcf86cd799439011")]
    [InlineData("o", "999999999999999999999", "507f1f77bcf86cd799439011")]
    [InlineData("o", "3155378976000000000", "507f1f77bcf86cd799439011")]
    [InlineData("n", "0", "000000000000000000000000")]
    [InlineData("n", "0", "not-an-id")]
    public void TryParsePageCursor_RejectsMalformedValues(
        string direction,
        string ticks,
        string menuId)
    {
        Assert.False(RoleMenuCustomIds.TryParsePageCursor(direction, ticks, menuId, out _));
    }

    [Fact]
    public void DeleteConfirm_BindsUserMenuAndVersionWithinDiscordLimit()
    {
        var menuId = ObjectId.GenerateNewId();

        var customId = RoleMenuCustomIds.DeleteConfirm(ulong.MaxValue, menuId, DateTime.MaxValue.Ticks);
        var parts = customId.Split(':');

        Assert.True(customId.Length <= 100);
        Assert.Equal(["role-menu", "delete-ok"], parts[..2]);
        Assert.True(RoleMenuCustomIds.TryParseMenuId(parts[3], out var parsedMenuId));
        Assert.Equal(menuId, parsedMenuId);
        Assert.True(RoleMenuCustomIds.TryParseMenuVersion(parts[4], out var version));
        Assert.Equal(DateTime.MaxValue.Ticks, version);
        Assert.False(RoleMenuCustomIds.TryParseMenuVersion("-5", out _));
        Assert.False(RoleMenuCustomIds.TryParseMenuVersion("+5", out _));
    }

    [Fact]
    public void TryParseManage_ReadsOnlyCanonicalManageButtons()
    {
        var menuId = ObjectId.GenerateNewId();

        Assert.True(RoleMenuCustomIds.TryParseManage(RoleMenuCustomIds.Manage(menuId), out var parsed));
        Assert.Equal(menuId, parsed);
        Assert.False(RoleMenuCustomIds.TryParseManage(null, out _));
        Assert.False(RoleMenuCustomIds.TryParseManage("role-menu:manage:", out _));
        Assert.False(RoleMenuCustomIds.TryParseManage($"role-menu:save:{menuId}", out _));
        Assert.False(RoleMenuCustomIds.TryParseManage($"role-menu:manage:{menuId}:extra", out _));
    }
}
