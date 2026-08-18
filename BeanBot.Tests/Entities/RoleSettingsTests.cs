using BeanBot.Entities;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;

using System.Text.Json;

using Xunit;

namespace BeanBot.Tests.Entities;

public class RoleSettingsTests
{
    [Fact]
    public void ParameterlessInstances_HaveSafeCollectionAndStringDefaults()
    {
        var settings = new RoleSettings();
        var pair = new RoleEmotePair();

        Assert.Empty(settings.RoleEmotePairs);
        Assert.Empty(settings.GuildId);
        Assert.Empty(settings.ChannelId);
        Assert.Empty(settings.MessageId);
        Assert.Empty(pair.RoleId);
        Assert.Empty(pair.EmojiId);
    }

    [Fact]
    public void Constructor_CopiesRoleEmotePairs()
    {
        var pairs = new List<RoleEmotePair> { new("role", "emoji") };
        var settings = new RoleSettings(pairs, "guild", "channel", "message");

        pairs.Clear();

        var pair = Assert.Single(settings.RoleEmotePairs);
        Assert.Equal("role", pair.RoleId);
        Assert.Equal("emoji", pair.EmojiId);
    }

    [Fact]
    public void Constructor_RejectsNullCollection()
    {
        Assert.Throws<ArgumentNullException>(
            () => new RoleSettings(null!, "guild", "channel", "message"));
    }

    [Fact]
    public void LegacyBson_DeserializesIntoModernProperties()
    {
        var id = ObjectId.GenerateNewId();
        var lastAccessedUtc = new DateTime(2026, 8, 18, 12, 34, 56, DateTimeKind.Utc);
        var legacyDocument = CreateLegacyDocument(id, lastAccessedUtc);

        var settings = BsonSerializer.Deserialize<RoleSettings>(legacyDocument);

        Assert.Equal(id, settings.Id);
        Assert.Equal("guild", settings.GuildId);
        Assert.Equal("channel", settings.ChannelId);
        Assert.Equal("message", settings.MessageId);
        Assert.Equal(lastAccessedUtc, settings.LastAccessedUtc);
        Assert.Equal(DateTimeKind.Utc, settings.LastAccessedUtc.Kind);
        var pair = Assert.Single(settings.RoleEmotePairs);
        Assert.Equal("role", pair.RoleId);
        Assert.Equal("emoji", pair.EmojiId);
    }

    [Fact]
    public void LegacyBson_MissingMembersRetainSafeDefaults()
    {
        var settings = BsonSerializer.Deserialize<RoleSettings>(
            new BsonDocument("_id", ObjectId.GenerateNewId()));

        Assert.Empty(settings.RoleEmotePairs);
        Assert.Empty(settings.GuildId);
        Assert.Empty(settings.ChannelId);
        Assert.Empty(settings.MessageId);
    }

    [Fact]
    public void LegacyBson_ExplicitNullMembersNormalizeToSafeDefaults()
    {
        var settings = BsonSerializer.Deserialize<RoleSettings>(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "roleEmotePair", BsonNull.Value },
                { "guildId", BsonNull.Value },
                { "channelId", BsonNull.Value },
                { "messageId", BsonNull.Value }
            });

        Assert.Empty(settings.RoleEmotePairs);
        Assert.Empty(settings.GuildId);
        Assert.Empty(settings.ChannelId);
        Assert.Empty(settings.MessageId);
    }

    [Fact]
    public void LegacyBson_ExplicitNullPairMembersNormalizeToSafeDefaults()
    {
        var pair = BsonSerializer.Deserialize<RoleEmotePair>(
            new BsonDocument
            {
                { "roleId", BsonNull.Value },
                { "emojiId", BsonNull.Value }
            });

        Assert.Empty(pair.RoleId);
        Assert.Empty(pair.EmojiId);
    }

    [Fact]
    public void BsonSerialization_PreservesExistingDocumentShapeAndTypes()
    {
        var id = ObjectId.GenerateNewId();
        var lastAccessedUtc = new DateTime(2026, 8, 18, 12, 34, 56, DateTimeKind.Utc);
        var settings = BsonSerializer.Deserialize<RoleSettings>(
            CreateLegacyDocument(id, lastAccessedUtc));

        var bson = settings.ToBsonDocument();

        Assert.Equal(
            ["_id", "roleEmotePair", "guildId", "channelId", "messageId", "lastAccessed"],
            bson.Names);
        Assert.Equal(BsonType.ObjectId, bson["_id"].BsonType);
        Assert.Equal(BsonType.Array, bson["roleEmotePair"].BsonType);
        Assert.Equal(BsonType.String, bson["guildId"].BsonType);
        Assert.Equal(BsonType.String, bson["channelId"].BsonType);
        Assert.Equal(BsonType.String, bson["messageId"].BsonType);
        Assert.Equal(BsonType.DateTime, bson["lastAccessed"].BsonType);

        var pair = bson["roleEmotePair"].AsBsonArray.Single().AsBsonDocument;
        Assert.Equal(["roleId", "emojiId"], pair.Names);
        Assert.Equal(BsonType.String, pair["roleId"].BsonType);
        Assert.Equal(BsonType.String, pair["emojiId"].BsonType);
        Assert.Equal(lastAccessedUtc, bson["lastAccessed"].ToUniversalTime());
        Assert.DoesNotContain("RoleEmotePairs", bson.Names);
        Assert.DoesNotContain("LastAccessedUtc", bson.Names);
    }

    [Fact]
    public void ToString_PreservesExistingJsonMemberNames()
    {
        var settings = new RoleSettings(
            new List<RoleEmotePair> { new("role", "emoji") },
            "guild",
            "channel",
            "message")
        {
            LastAccessedUtc = new DateTime(2026, 8, 18, 12, 34, 56, DateTimeKind.Utc)
        };

        var json = settings.ToString();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("roleEmotePair", out var pairs));
        Assert.True(root.TryGetProperty("guildId", out _));
        Assert.True(root.TryGetProperty("channelId", out _));
        Assert.True(root.TryGetProperty("messageId", out _));
        Assert.True(root.TryGetProperty("lastAccessed", out _));
        Assert.False(root.TryGetProperty("RoleEmotePairs", out _));
        Assert.False(root.TryGetProperty("LastAccessedUtc", out _));

        var pair = pairs.EnumerateArray().Single();
        Assert.Equal("role", pair.GetProperty("roleId").GetString());
        Assert.Equal("emoji", pair.GetProperty("emojiId").GetString());
    }

    private static BsonDocument CreateLegacyDocument(ObjectId id, DateTime lastAccessedUtc)
        => new()
        {
            { "_id", id },
            {
                "roleEmotePair",
                new BsonArray
                {
                    new BsonDocument
                    {
                        { "roleId", "role" },
                        { "emojiId", "emoji" }
                    }
                }
            },
            { "guildId", "guild" },
            { "channelId", "channel" },
            { "messageId", "message" },
            { "lastAccessed", new BsonDateTime(lastAccessedUtc) }
        };
}
