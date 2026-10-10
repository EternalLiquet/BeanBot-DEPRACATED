using BeanBot.Configuration;
using BeanBot.Discord.RoleMenus;
using BeanBot.Health;
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
        services.AddSingleton(provider => InteractionCommandRegistrationTarget.FromGuildId(
            provider.GetRequiredService<BeanBotOptions>().InteractionGuildId));
        services.AddSingleton<DiscordRoleMenuClient>();
        services.AddSingleton<LegacyReactionRoleMigrationClient>();
        services.AddSingleton<RoleMenuMemberService>();
        services.AddSingleton<RoleMenuAdministrationService>();
        services.AddSingleton<RoleMenuMigrationService>();
        services.AddSingleton<RoleMenuAuditService>();
        services.AddSingleton<InteractionHandler>();
        services.AddSingleton<BeanBotInteractionHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<BeanBotInteractionHostedService>());
        services.AddSingleton<ApplicationReadinessHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<ApplicationReadinessHostedService>());

        return services;
    }
}
