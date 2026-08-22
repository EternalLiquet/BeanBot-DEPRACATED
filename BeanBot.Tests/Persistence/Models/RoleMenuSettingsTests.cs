using BeanBot.Persistence.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Xunit;

namespace BeanBot.Tests.Persistence.Models;

public class RoleMenuSettingsTests
{
    [Fact]
    public void BsonRoundTrip_PreservesRestartSafeConfigurationAndStringMode()
    {
        var expected = new RoleMenuSettings(
            ObjectId.GenerateNewId(),
            "1",
            "2",
            "3",
            "Games",
            "Choose games",
            ["4", "5"],
            RoleMenuSelectionMode.Single)
        {
            CreatedAtUtc = new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 8, 22, 12, 1, 0, DateTimeKind.Utc)
        };

        var document = expected.ToBsonDocument();
        var actual = BsonSerializer.Deserialize<RoleMenuSettings>(document);

        Assert.Equal("Single", document["selectionMode"].AsString);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.GuildId, actual.GuildId);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.RoleIds, actual.RoleIds);
        Assert.Equal(expected.SelectionMode, actual.SelectionMode);
        Assert.Equal(DateTimeKind.Utc, actual.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, actual.UpdatedAtUtc.Kind);
    }

    [Fact]
    public void Constructor_RejectsEmptyMenuId()
    {
        Assert.Throws<ArgumentException>(() => new RoleMenuSettings(
            ObjectId.Empty,
            "1",
            "2",
            "3",
            "Games",
            string.Empty,
            ["4"],
            RoleMenuSelectionMode.Multiple));
    }
}
