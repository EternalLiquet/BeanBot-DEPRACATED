using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BeanBot.Entities;

public class RoleSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private List<RoleEmotePair> _roleEmotePairs = [];
    private string _guildId = string.Empty;
    private string _channelId = string.Empty;
    private string _messageId = string.Empty;

    [BsonId]
    [JsonPropertyName("id")]
    public ObjectId Id { get; init; }

    [BsonElement("roleEmotePair")]
    [JsonPropertyName("roleEmotePair")]
    public List<RoleEmotePair> RoleEmotePairs
    {
        get => _roleEmotePairs;
        init => _roleEmotePairs = value ?? [];
    }

    [BsonElement("guildId")]
    [JsonPropertyName("guildId")]
    public string GuildId
    {
        get => _guildId;
        init => _guildId = value ?? string.Empty;
    }

    [BsonElement("channelId")]
    [JsonPropertyName("channelId")]
    public string ChannelId
    {
        get => _channelId;
        init => _channelId = value ?? string.Empty;
    }

    [BsonElement("messageId")]
    [JsonPropertyName("messageId")]
    public string MessageId
    {
        get => _messageId;
        init => _messageId = value ?? string.Empty;
    }

    [BsonElement("lastAccessed")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [JsonPropertyName("lastAccessed")]
    public DateTime LastAccessedUtc { get; internal set; }

    public RoleSettings() { }

    public RoleSettings(
        List<RoleEmotePair> roleEmotePairs,
        string guildId,
        string channelId,
        string messageId)
    {
        RoleEmotePairs = [.. roleEmotePairs ?? throw new ArgumentNullException(nameof(roleEmotePairs))];
        GuildId = guildId ?? throw new ArgumentNullException(nameof(guildId));
        ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
    }

    public override string ToString()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }
}
