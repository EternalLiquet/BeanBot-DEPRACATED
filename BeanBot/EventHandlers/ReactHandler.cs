using BeanBot.Services;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace BeanBot.EventHandlers;

public sealed class ReactHandler(
    GatewayClient client,
    RoleReactService roleReactService,
    GuildUserResolverService guildUserResolverService,
    GuildRoleManagementService guildRoleManagementService,
    ILogger<ReactHandler> logger) :
    IMessageReactionAddGatewayHandler,
    IMessageReactionRemoveGatewayHandler
{
    public ValueTask HandleAsync(MessageReactionAddEventArgs arg)
        => HandleAsync(arg.UserId, arg.GuildId, arg.ChannelId, arg.MessageId, arg.Emoji.Id, arg.Emoji.Name, removeRole: false);

    public ValueTask HandleAsync(MessageReactionRemoveEventArgs arg)
        => HandleAsync(arg.UserId, arg.GuildId, arg.ChannelId, arg.MessageId, arg.Emoji.Id, arg.Emoji.Name, removeRole: true);

    private async ValueTask HandleAsync(ulong userId, ulong? guildId, ulong channelId, ulong messageId, ulong? emojiId, string? emojiName, bool removeRole)
    {
        if (client.Cache.User is not { } currentUser || userId == currentUser.Id)
        {
            return;
        }

        if (guildId is null)
        {
            return;
        }

        if (!client.Cache.Guilds.TryGetValue(guildId.Value, out var guild) ||
            !guild.Channels.TryGetValue(channelId, out var channel) ||
            channel is not TextChannel textChannel)
        {
            logger.LogDebug("Skipping reaction event for message {MessageId} because the guild or channel was not found in cache", messageId);
            return;
        }

        var trackedMessage = await textChannel.GetMessageAsync(messageId);
        if (trackedMessage.Author is null || trackedMessage.Author.Id != currentUser.Id)
        {
            return;
        }

        var roleId = await roleReactService.TryResolveRoleIdAsync(messageId, emojiId, emojiName);
        if (roleId is not ulong resolvedRoleId)
        {
            return;
        }

        var guildUser = await guildUserResolverService.ResolveGuildUserAsync(
            client,
            guild,
            userId,
            $"reaction role handling for message {messageId}");
        if (guildUser is null)
        {
            logger.LogWarning(
                "Skipping role reaction for user {UserId}; guild user could not be resolved for guild {GuildId} and message {MessageId}",
                userId,
                guild.Id,
                messageId);
            return;
        }

        if (!guild.Roles.TryGetValue(resolvedRoleId, out var role))
        {
            logger.LogWarning("Skipping role reaction for message {MessageId}; role {RoleId} no longer exists", messageId, resolvedRoleId);
            return;
        }

        var botGuildUser = await guildUserResolverService.ResolveCurrentGuildUserAsync(
            client,
            guild,
            $"reaction role handling for message {messageId}");
        if (botGuildUser is null)
        {
            logger.LogWarning(
                "Skipping role reaction for message {MessageId}; bot guild user could not be resolved in guild {GuildId}",
                messageId,
                guild.Id);
            return;
        }

        if (!guildRoleManagementService.CanModifyMember(guild, botGuildUser, guildUser, role, out var roleMutationFailure))
        {
            logger.LogWarning(
                "Skipping role reaction for message {MessageId}; cannot mutate role {RoleId} for user {UserId} in guild {GuildId}: {FailureReason}",
                messageId,
                resolvedRoleId,
                guildUser.Id,
                guild.Id,
                roleMutationFailure);
            return;
        }

        if (removeRole)
        {
            if (!guildUser.RoleIds.Contains(resolvedRoleId))
            {
                return;
            }

            logger.LogDebug("Removing role {RoleId} from user {UserId} for message {MessageId}", resolvedRoleId, guildUser.Id, messageId);
            await guildUser.RemoveRoleAsync(resolvedRoleId);
            return;
        }

        if (guildUser.RoleIds.Contains(resolvedRoleId))
        {
            return;
        }

        logger.LogDebug("Adding role {RoleId} to user {UserId} for message {MessageId}", resolvedRoleId, guildUser.Id, messageId);
        await guildUser.AddRoleAsync(resolvedRoleId);
    }
}
