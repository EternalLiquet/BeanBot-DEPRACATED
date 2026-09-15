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
            RoleMenuSelectionMode.Exclusive,
            "99")
        {
            CreatedAtUtc = new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 8, 22, 12, 1, 0, DateTimeKind.Utc)
        };

        var document = expected.ToBsonDocument();
        var actual = BsonSerializer.Deserialize<RoleMenuSettings>(document);

        Assert.Equal("Exclusive", document["selectionMode"].AsString);
        Assert.Equal("99", document["migratedFromReactionRoleMessageId"].AsString);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.GuildId, actual.GuildId);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.RoleIds, actual.RoleIds);
        Assert.Equal(expected.SelectionMode, actual.SelectionMode);
        Assert.Equal(expected.MigratedFromReactionRoleMessageId, actual.MigratedFromReactionRoleMessageId);
        Assert.Equal(DateTimeKind.Utc, actual.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, actual.UpdatedAtUtc.Kind);
    }

    [Fact]
    public void BsonSerialize_NonMigratedRoleMenuOmitsMigrationProvenance()
    {
        var settings = new RoleMenuSettings(
            ObjectId.GenerateNewId(),
            "1",
            "2",
            "3",
            "Games",
            string.Empty,
            ["4"],
            RoleMenuSelectionMode.Multiple);

        var document = settings.ToBsonDocument();

        Assert.False(document.Contains("migratedFromReactionRoleMessageId"));
    }

    [Fact]
    public void BsonDeserialize_LegacyRoleMenuWithoutMigrationProvenanceDefaultsEmpty()
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["guildId"] = "1",
            ["channelId"] = "2",
            ["messageId"] = "3",
            ["title"] = "Games",
            ["description"] = string.Empty,
            ["roleIds"] = new BsonArray(["4"]),
            ["selectionMode"] = "Multiple"
        };

        var settings = BsonSerializer.Deserialize<RoleMenuSettings>(document);

        Assert.Equal(string.Empty, settings.MigratedFromReactionRoleMessageId);
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
