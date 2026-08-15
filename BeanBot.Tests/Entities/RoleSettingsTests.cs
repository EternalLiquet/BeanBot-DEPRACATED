using BeanBot.Entities;

using MongoDB.Bson;

using Newtonsoft.Json.Linq;

using Xunit;

namespace BeanBot.Tests.Entities;

public class RoleSettingsTests
{
    [Fact]
    public void ParameterlessInstance_HasSafeCollectionAndStringDefaults()
    {
        var settings = new RoleSettings();

        Assert.Empty(settings.roleEmotePair);
        Assert.Empty(settings.guildId);
        Assert.Empty(settings.channelId);
        Assert.Empty(settings.messageId);
    }

    [Fact]
    public void Serialization_PreservesExistingPersistedMemberNames()
    {
        var settings = new RoleSettings(
            new List<RoleEmotePair> { new("role", "emoji") },
            "guild",
            "channel",
            "message");

        var bson = settings.ToBsonDocument();
        var json = JObject.Parse(settings.ToString());

        Assert.True(bson.Contains("_id"));
        Assert.True(bson.Contains("roleEmotePair"));
        Assert.True(bson.Contains("guildId"));
        Assert.True(bson.Contains("channelId"));
        Assert.True(bson.Contains("messageId"));
        Assert.True(bson.Contains("lastAccessed"));
        Assert.NotNull(json["roleEmotePair"]);
        Assert.NotNull(json["guildId"]);
        Assert.NotNull(json["channelId"]);
        Assert.NotNull(json["messageId"]);
    }
}
