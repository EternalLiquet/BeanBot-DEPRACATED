using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuMigrationIdentity
{
    internal static ObjectId CreateMenuId(ulong guildId, ulong legacyMessageId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(guildId);
        ArgumentOutOfRangeException.ThrowIfZero(legacyMessageId);

        var source = string.Create(
            CultureInfo.InvariantCulture,
            $"legacy-reaction-role:{guildId}:{legacyMessageId}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        var objectIdHex = Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
        return ObjectId.Parse(objectIdHex);
    }

    internal static string BuildMessageLink(ulong guildId, ulong channelId, ulong messageId)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"https://discord.com/channels/{guildId}/{channelId}/{messageId}");
}
