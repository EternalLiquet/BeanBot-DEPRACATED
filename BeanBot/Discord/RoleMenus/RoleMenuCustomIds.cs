using System.Globalization;
using Discord;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuCustomIds
{
    internal const string CreateModal = "role-menu:create";
    internal const string ManagePattern = "role-menu:manage:*";
    internal const string SavePattern = "role-menu:save:*:*:*";
    internal const string ClearPattern = "role-menu:clear:*:*:*";
    internal const string PublishPattern = "role-menu:publish:*";
    internal const string CancelPublishPattern = "role-menu:cancel-publish:*";
    internal const string DeleteSelectPattern = "role-menu:delete-select:*";
    internal const string DeleteConfirmPattern = "role-menu:delete-confirm:*:*";
    internal const string DeleteCancelPattern = "role-menu:delete-cancel:*";

    internal static string Manage(ObjectId menuId)
        => EnsureValid($"role-menu:manage:{menuId}");

    internal static string Save(ObjectId menuId, ulong userId, ulong panelMessageId)
        => EnsureValid(
            $"role-menu:save:{menuId}:" +
            $"{userId.ToString(CultureInfo.InvariantCulture)}:" +
            panelMessageId.ToString(CultureInfo.InvariantCulture));

    internal static string Clear(ObjectId menuId, ulong userId, ulong panelMessageId)
        => EnsureValid(
            $"role-menu:clear:{menuId}:" +
            $"{userId.ToString(CultureInfo.InvariantCulture)}:" +
            panelMessageId.ToString(CultureInfo.InvariantCulture));

    internal static string Publish(Guid draftId)
        => EnsureValid($"role-menu:publish:{draftId:N}");

    internal static string CancelPublish(Guid draftId)
        => EnsureValid($"role-menu:cancel-publish:{draftId:N}");

    internal static string DeleteSelect(ulong userId)
        => EnsureValid($"role-menu:delete-select:{userId.ToString(CultureInfo.InvariantCulture)}");

    internal static string DeleteConfirm(ulong userId, ObjectId menuId)
        => EnsureValid(
            $"role-menu:delete-confirm:{userId.ToString(CultureInfo.InvariantCulture)}:{menuId}");

    internal static string DeleteCancel(ulong userId)
        => EnsureValid($"role-menu:delete-cancel:{userId.ToString(CultureInfo.InvariantCulture)}");

    internal static bool TryParseMenuId(string value, out ObjectId menuId)
    {
        menuId = ObjectId.Empty;
        return value is { Length: 24 }
            && ObjectId.TryParse(value, out menuId)
            && string.Equals(value, menuId.ToString(), StringComparison.Ordinal)
            && menuId != ObjectId.Empty;
    }

    internal static bool TryParseDraftId(string value, out Guid draftId)
        => Guid.TryParseExact(value, "N", out draftId) && draftId != Guid.Empty;

    internal static bool TryParseSnowflake(string value, out ulong snowflake)
        => ulong.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out snowflake)
            && snowflake != 0;

    private static string EnsureValid(string customId)
    {
        if (customId.Length > ComponentBuilder.MaxCustomIdLength)
        {
            throw new InvalidOperationException(
                $"Role-menu custom ID exceeded {ComponentBuilder.MaxCustomIdLength} characters.");
        }

        return customId;
    }
}
