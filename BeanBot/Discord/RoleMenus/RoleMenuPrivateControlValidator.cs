using Discord;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuPrivateControlIssue
{
    None,
    InvalidMenuId,
    InvalidUserId,
    InvalidPanelMessageId,
    WrongUser,
    NotEphemeral,
    WrongComponentType,
    UnexpectedAuthor,
    MissingSourceComponent
}

internal readonly record struct RoleMenuPrivateControlBinding(
    ObjectId MenuId,
    ulong PanelMessageId);

internal static class RoleMenuPrivateControlValidator
{
    internal static RoleMenuPrivateControlIssue Validate(
        string menuIdValue,
        string userIdValue,
        string panelMessageIdValue,
        ulong interactingUserId,
        bool isEphemeral,
        ComponentType componentType,
        ComponentType expectedComponentType,
        ulong messageAuthorId,
        ulong botUserId,
        bool hasSourceComponent,
        out RoleMenuPrivateControlBinding binding)
    {
        binding = default;
        if (!RoleMenuCustomIds.TryParseMenuId(menuIdValue, out var menuId))
        {
            return RoleMenuPrivateControlIssue.InvalidMenuId;
        }

        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId))
        {
            return RoleMenuPrivateControlIssue.InvalidUserId;
        }

        if (!RoleMenuCustomIds.TryParseSnowflake(
                panelMessageIdValue,
                out var panelMessageId))
        {
            return RoleMenuPrivateControlIssue.InvalidPanelMessageId;
        }

        if (boundUserId != interactingUserId)
        {
            return RoleMenuPrivateControlIssue.WrongUser;
        }

        if (!isEphemeral)
        {
            return RoleMenuPrivateControlIssue.NotEphemeral;
        }

        if (componentType != expectedComponentType)
        {
            return RoleMenuPrivateControlIssue.WrongComponentType;
        }

        if (messageAuthorId != botUserId)
        {
            return RoleMenuPrivateControlIssue.UnexpectedAuthor;
        }

        if (!hasSourceComponent)
        {
            return RoleMenuPrivateControlIssue.MissingSourceComponent;
        }

        binding = new RoleMenuPrivateControlBinding(menuId, panelMessageId);
        return RoleMenuPrivateControlIssue.None;
    }
}
