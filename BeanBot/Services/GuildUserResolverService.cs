using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;

namespace BeanBot.Services;

public sealed class GuildUserResolverService(ILogger<GuildUserResolverService> logger)
{
    public async Task<GuildUser?> ResolveGuildUserAsync(
        GatewayClient client,
        Guild guild,
        ulong userId,
        string context,
        CancellationToken cancellationToken = default)
    {
        if (guild.Users.TryGetValue(userId, out var cachedGuildUser))
        {
            logger.LogDebug(
                "Resolved guild user {UserId} in guild {GuildId} from cache during {Context}",
                userId,
                guild.Id,
                context);
            return cachedGuildUser;
        }

        logger.LogInformation(
            "Guild user {UserId} was missing from cache in guild {GuildId} during {Context}. Falling back to REST. CachedUserCount={CachedUserCount}",
            userId,
            guild.Id,
            context,
            guild.Users.Count);

        try
        {
            var guildUser = await client.Rest.GetGuildUserAsync(guild.Id, userId, default, cancellationToken);
            logger.LogInformation(
                "Resolved guild user {UserId} in guild {GuildId} via REST during {Context}",
                userId,
                guild.Id,
                context);
            return guildUser;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to resolve guild user {UserId} in guild {GuildId} via REST during {Context}",
                userId,
                guild.Id,
                context);
            return null;
        }
    }

    public async Task<GuildUser?> ResolveCurrentGuildUserAsync(
        GatewayClient client,
        Guild guild,
        string context,
        CancellationToken cancellationToken = default)
    {
        if (guild.Users.TryGetValue(client.Id, out var cachedGuildUser))
        {
            logger.LogDebug(
                "Resolved current bot guild user {UserId} in guild {GuildId} from cache during {Context}",
                client.Id,
                guild.Id,
                context);
            return cachedGuildUser;
        }

        logger.LogInformation(
            "Current bot guild user {UserId} was missing from cache in guild {GuildId} during {Context}. Falling back to REST. CachedUserCount={CachedUserCount}",
            client.Id,
            guild.Id,
            context,
            guild.Users.Count);

        try
        {
            var guildUser = await client.Rest.GetCurrentUserGuildUserAsync(guild.Id, default, cancellationToken);
            logger.LogInformation(
                "Resolved current bot guild user {UserId} in guild {GuildId} via REST during {Context}",
                client.Id,
                guild.Id,
                context);
            return guildUser;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to resolve current bot guild user {UserId} in guild {GuildId} via REST during {Context}",
                client.Id,
                guild.Id,
                context);
            return null;
        }
    }
}
