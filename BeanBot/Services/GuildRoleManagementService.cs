using NetCord;
using NetCord.Gateway;

namespace BeanBot.Services;

public sealed class GuildRoleManagementService
{
    public bool CanManageRole(Guild guild, GuildUser botUser, Role role, out string reason)
    {
        var everyoneRole = guild.EveryoneRole;
        if (everyoneRole is not null && role.Id == everyoneRole.Id)
        {
            reason = "The @everyone role cannot be assigned through reaction roles.";
            return false;
        }

        if (role.Managed)
        {
            reason = $"The role `{role.Name}` is managed by Discord or an integration and cannot be assigned manually.";
            return false;
        }

        var botHighestRolePosition = GetHighestRolePosition(guild, botUser);
        if (role.RawPosition >= botHighestRolePosition)
        {
            reason = $"The role `{role.Name}` is not below the bot's highest role in the server hierarchy.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool CanModifyMember(Guild guild, GuildUser botUser, GuildUser targetUser, Role role, out string reason)
    {
        if (!CanManageRole(guild, botUser, role, out reason))
        {
            return false;
        }

        var botHighestRolePosition = GetHighestRolePosition(guild, botUser);
        var targetHighestRolePosition = GetHighestRolePosition(guild, targetUser);
        if (targetHighestRolePosition >= botHighestRolePosition)
        {
            reason = $"The target user's highest role is not below the bot's highest role in the server hierarchy.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public int GetHighestRolePosition(Guild guild, GuildUser user)
    {
        var highestRolePosition = guild.EveryoneRole?.RawPosition ?? 0;
        foreach (var roleId in user.RoleIds)
        {
            if (guild.Roles.TryGetValue(roleId, out var role) && role.RawPosition > highestRolePosition)
            {
                highestRolePosition = role.RawPosition;
            }
        }

        return highestRolePosition;
    }
}
