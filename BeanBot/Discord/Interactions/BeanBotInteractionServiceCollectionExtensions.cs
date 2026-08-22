using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Repositories;
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
                LogLevel = LogSeverity.Verbose,
                UseCompiledLambda = true
            }));
        services.AddSingleton(_ => new InteractionExecutionContext());
        services.AddSingleton(provider => new RoleMenuInteractionService(
            provider.GetRequiredService<RoleMenuRepository>(),
            provider.GetRequiredService<RoleMenuDraftRegistry>(),
            provider.GetRequiredService<RoleMenuMemberSynchronizer>(),
            provider.GetRequiredService<RoleMenuMutationCoordinator>(),
            provider.GetRequiredService<InteractionExecutionContext>()));
        services.AddSingleton<InteractionHandler>();
        services.AddSingleton<BeanBotInteractionHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<BeanBotInteractionHostedService>());

        return services;
    }
}
