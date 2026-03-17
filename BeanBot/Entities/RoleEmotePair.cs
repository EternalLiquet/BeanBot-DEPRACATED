using BeanBot.Util;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BeanBot.Entities;

public class RoleEmotePair
{
    [BsonElement("roleId")]
    [BsonRepresentation(BsonType.String)]
    public ulong RoleId { get; set; }

    [BsonElement("emojiId")]
    [BsonRepresentation(BsonType.String)]
    [BsonIgnoreIfDefault]
    public ulong EmojiId { get; set; }

    [BsonElement("emojiKey")]
    public string EmojiKey { get; set; } = string.Empty;

    public RoleEmotePair()
    {
    }

    public RoleEmotePair(ulong roleId, ulong emojiId)
    {
        RoleId = roleId;
        EmojiId = emojiId;
        EmojiKey = ReactionEmojiKey.FromCustomEmoji(emojiId);
    }

    public RoleEmotePair(ulong roleId, string emojiKey)
    {
        RoleId = roleId;
        EmojiKey = emojiKey;
    }

    public string ResolveEmojiKey()
    {
        if (!string.IsNullOrWhiteSpace(EmojiKey))
        {
            return EmojiKey;
        }

        return EmojiId == 0
            ? string.Empty
            : ReactionEmojiKey.FromCustomEmoji(EmojiId);
    }
}
