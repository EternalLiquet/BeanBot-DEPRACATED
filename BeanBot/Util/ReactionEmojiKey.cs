namespace BeanBot.Util;

public static class ReactionEmojiKey
{
    private const string CustomEmojiPrefix = "custom:";
    private const string StandardEmojiPrefix = "standard:";

    public static string FromCustomEmoji(ulong emojiId)
        => $"{CustomEmojiPrefix}{emojiId}";

    public static string FromStandardEmoji(string emoji)
    {
        var normalizedEmoji = emoji.Trim();
        return string.IsNullOrWhiteSpace(normalizedEmoji)
            ? string.Empty
            : $"{StandardEmojiPrefix}{normalizedEmoji}";
    }

    public static string Create(ulong? emojiId, string? emojiName)
    {
        if (emojiId is ulong resolvedEmojiId)
        {
            return FromCustomEmoji(resolvedEmojiId);
        }

        return string.IsNullOrWhiteSpace(emojiName)
            ? string.Empty
            : FromStandardEmoji(emojiName);
    }
}
