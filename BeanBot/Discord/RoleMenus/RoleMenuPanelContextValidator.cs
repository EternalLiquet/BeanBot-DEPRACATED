namespace BeanBot.Discord.RoleMenus;

internal enum RoleMenuPanelContextIssue
{
    None,
    GuildMismatch,
    ChannelMismatch,
    MessageMismatch,
    UnexpectedAuthor,
    MissingManageButton
}

internal static class RoleMenuPanelContextValidator
{
    internal static RoleMenuPanelContextIssue Validate(
        ParsedRoleMenuSettings settings,
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong messageAuthorId,
        ulong botUserId,
        bool hasManageButton)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GuildId != guildId)
        {
            return RoleMenuPanelContextIssue.GuildMismatch;
        }

        if (settings.ChannelId != channelId)
        {
            return RoleMenuPanelContextIssue.ChannelMismatch;
        }

        if (settings.MessageId != messageId)
        {
            return RoleMenuPanelContextIssue.MessageMismatch;
        }

        if (messageAuthorId != botUserId)
        {
            return RoleMenuPanelContextIssue.UnexpectedAuthor;
        }

        return hasManageButton
            ? RoleMenuPanelContextIssue.None
            : RoleMenuPanelContextIssue.MissingManageButton;
    }
}
