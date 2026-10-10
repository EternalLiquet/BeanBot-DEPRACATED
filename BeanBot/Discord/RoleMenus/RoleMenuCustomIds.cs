using System.Globalization;
using BeanBot.Persistence.Repositories;
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
    internal const string DeletePagePattern = "role-menu:delete-page:*:*:*:*";
    internal const string DeleteConfirmPattern = "role-menu:delete-ok:*:*:*";
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

    internal static string DeletePage(ulong userId, RoleMenuPageCursor cursor)
        => EnsureValid(
            $"role-menu:delete-page:{userId.ToString(CultureInfo.InvariantCulture)}:" +
            (cursor.Direction == RoleMenuPageDirection.Newer ? "n" : "o") + ":" +
            $"{cursor.CreatedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}:{cursor.MenuId}");

    internal static string DeleteConfirm(ulong userId, ObjectId menuId, long menuVersion)
        => EnsureValid(
            $"role-menu:delete-ok:{userId.ToString(CultureInfo.InvariantCulture)}:{menuId}:" +
            menuVersion.ToString(CultureInfo.InvariantCulture));

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

    internal static bool TryParseManage(string? customId, out ObjectId menuId)
    {
        const string prefix = "role-menu:manage:";
        menuId = ObjectId.Empty;
        return customId is not null
            && customId.StartsWith(prefix, StringComparison.Ordinal)
            && TryParseMenuId(customId[prefix.Length..], out menuId);
    }

    internal static bool TryParsePageCursor(
        string directionValue,
        string createdAtTicksValue,
        string menuIdValue,
        out RoleMenuPageCursor cursor)
    {
        cursor = default;
        RoleMenuPageDirection direction;
        switch (directionValue)
        {
            case "o":
                direction = RoleMenuPageDirection.Older;
                break;
            case "n":
                direction = RoleMenuPageDirection.Newer;
                break;
            default:
                return false;
        }

        if (!TryParseTicks(createdAtTicksValue, out var ticks)
            || ticks > DateTime.MaxValue.Ticks
            || !TryParseMenuId(menuIdValue, out var menuId))
        {
            return false;
        }

        cursor = new RoleMenuPageCursor(new DateTime(ticks, DateTimeKind.Utc), menuId, direction);
        return true;
    }

    internal static bool TryParseMenuVersion(string value, out long menuVersion)
        => TryParseTicks(value, out menuVersion);

    internal static bool TryParseDraftId(string value, out Guid draftId)
        => Guid.TryParseExact(value, "N", out draftId) && draftId != Guid.Empty;

    internal static bool TryParseSnowflake(string value, out ulong snowflake)
        => ulong.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out snowflake)
            && snowflake != 0;

    private static bool TryParseTicks(string value, out long ticks)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ticks)
           && string.Equals(value, ticks.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

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
