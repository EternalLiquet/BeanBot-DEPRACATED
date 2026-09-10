using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BeanBot.Persistence.Models;

public enum RoleMenuSelectionMode
{
    Multiple,
    Exclusive
}

[BsonIgnoreExtraElements]
public sealed class RoleMenuSettings
{
    private List<string> _roleIds = [];
    private string _guildId = string.Empty;
    private string _channelId = string.Empty;
    private string _messageId = string.Empty;
    private string _title = string.Empty;
    private string _description = string.Empty;

    [BsonId]
    [JsonPropertyName("id")]
    public ObjectId Id { get; init; } = ObjectId.GenerateNewId();

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

    [BsonElement("title")]
    [JsonPropertyName("title")]
    public string Title
    {
        get => _title;
        init => _title = value ?? string.Empty;
    }

    [BsonElement("description")]
    [JsonPropertyName("description")]
    public string Description
    {
        get => _description;
        init => _description = value ?? string.Empty;
    }

    [BsonElement("roleIds")]
    [JsonPropertyName("roleIds")]
    public List<string> RoleIds
    {
        get => _roleIds;
        init => _roleIds = value ?? [];
    }

    [BsonElement("selectionMode")]
    [BsonRepresentation(BsonType.String)]
    [JsonPropertyName("selectionMode")]
    public RoleMenuSelectionMode SelectionMode { get; init; }

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; internal set; }

    [BsonElement("updatedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; internal set; }

    public RoleMenuSettings() { }

    public RoleMenuSettings(
        ObjectId id,
        string guildId,
        string channelId,
        string messageId,
        string title,
        string description,
        IEnumerable<string> roleIds,
        RoleMenuSelectionMode selectionMode)
    {
        if (id == ObjectId.Empty)
        {
            throw new ArgumentException("A role menu ID is required.", nameof(id));
        }

        Id = id;
        GuildId = guildId ?? throw new ArgumentNullException(nameof(guildId));
        ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        RoleIds = [.. roleIds ?? throw new ArgumentNullException(nameof(roleIds))];
        SelectionMode = selectionMode;
    }
}
