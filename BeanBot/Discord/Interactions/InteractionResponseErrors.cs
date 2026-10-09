using System.Net;
using Discord;
using Discord.Net;

namespace BeanBot.Discord.Interactions;

internal static class InteractionResponseErrors
{
    internal static bool IsKnownMissingOriginal(Exception exception)
        => exception is HttpException
        {
            HttpCode: HttpStatusCode.NotFound,
            DiscordCode: DiscordErrorCode.UnknownWebhook
        };
}
