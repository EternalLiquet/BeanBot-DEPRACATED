using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

namespace BeanBot.EventHandlers;

public sealed class NewMemberHandler(ILogger<NewMemberHandler> logger) : IGuildUserAddGatewayHandler
{
    public async ValueTask HandleAsync(GuildUser user)
    {
        if (user.IsBot)
        {
            return;
        }

        try
        {
            var dmChannel = await user.GetDMChannelAsync();
            await dmChannel.SendMessageAsync(new MessageProperties
            {
                Content = "Please read the rules in the Eli's Charter channel. If you agree to these rules and are over the age of 17, please DM one of the moderators with the blue role \"Student Council\" (i.e discount Hatate/Makoto Kikuchi#2351) for full access to the server! (I promise it's worth it)",
            });

            logger.LogDebug("Sent onboarding DM to user {UserId}", user.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to send onboarding DM to user {UserId}", user.Id);
        }
    }
}
