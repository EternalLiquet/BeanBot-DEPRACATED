using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace BeanBot.Entities;

public class RoleEmotePair
{
    private string _roleId = string.Empty;
    private string _emojiId = string.Empty;

    [BsonElement("roleId")]
    [JsonPropertyName("roleId")]
    public string RoleId
    {
        get => _roleId;
        init => _roleId = value ?? string.Empty;
    }

    [BsonElement("emojiId")]
    [JsonPropertyName("emojiId")]
    public string EmojiId
    {
        get => _emojiId;
        init => _emojiId = value ?? string.Empty;
    }

    public RoleEmotePair() { }

    public RoleEmotePair(string roleId, string emojiId)
    {
        RoleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
        EmojiId = emojiId ?? throw new ArgumentNullException(nameof(emojiId));
    }
}
