using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json;

namespace BeanBot.Entities;

public class RoleSettings
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("roleEmotePair")]
    public List<RoleEmotePair> RoleEmotePairs { get; set; } = [];

    [BsonElement("guildId")]
    [BsonRepresentation(BsonType.String)]
    public ulong GuildId { get; set; }

    [BsonElement("channelId")]
    [BsonRepresentation(BsonType.String)]
    public ulong ChannelId { get; set; }

    [BsonElement("messageId")]
    [BsonRepresentation(BsonType.String)]
    public ulong MessageId { get; set; }

    [BsonElement("lastAccessed")]
    public DateTime LastAccessed { get; set; }

    public RoleSettings()
    {
    }

    public RoleSettings(IEnumerable<RoleEmotePair> roleEmotePairs, ulong guildId, ulong channelId, ulong messageId)
    {
        RoleEmotePairs = roleEmotePairs.ToList();
        GuildId = guildId;
        ChannelId = channelId;
        MessageId = messageId;
        LastAccessed = DateTime.UtcNow;
    }

    public override string ToString()
        => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}
