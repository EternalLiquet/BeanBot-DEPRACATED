using Discord;
using Discord.WebSocket;

namespace BeanBot.Hosting
{
    internal static class DiscordSocketConfiguration
    {
        internal static DiscordSocketConfig Create()
        {
            return new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Verbose,
                MessageCacheSize = 50,
                AlwaysDownloadUsers = false,
                GatewayIntents = GatewayIntents.Guilds
                    | GatewayIntents.GuildMembers
                    | GatewayIntents.GuildEmojis
                    | GatewayIntents.GuildMessages
                    | GatewayIntents.DirectMessages
                    | GatewayIntents.GuildMessageReactions
                    | GatewayIntents.DirectMessageReactions
                    | GatewayIntents.MessageContent
            };
        }
    }
}
