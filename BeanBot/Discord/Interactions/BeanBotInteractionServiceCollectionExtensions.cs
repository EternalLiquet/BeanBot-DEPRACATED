using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeanBot.Discord.Interactions;

internal static class BeanBotInteractionServiceCollectionExtensions
{
    internal static IServiceCollection AddBeanBotInteractions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => new InteractionService(
            provider.GetRequiredService<DiscordSocketClient>().Rest,
            new InteractionServiceConfig
            {
                LogLevel = LogSeverity.Verbose
            }));
        services.AddSingleton<InteractionHandler>();
        services.AddSingleton<BeanBotInteractionHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<BeanBotInteractionHostedService>());

        return services;
    }
}
