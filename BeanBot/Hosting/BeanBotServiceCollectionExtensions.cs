using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;
using BeanBot.Discord.Lifecycle;
using BeanBot.Discord.Messaging;
using BeanBot.Discord.ReactionRoles;
using BeanBot.Health;
using BeanBot.Logging;
using BeanBot.Persistence;
using BeanBot.Persistence.Outages;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace BeanBot.Hosting;

internal static class BeanBotServiceCollectionExtensions
{
    internal static IServiceCollection AddBeanBot(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Register the client as an existing singleton so the host container does not
        // dispose it automatically. BeanBotApplication owns its conditional teardown
        // because a timed-out Discord.Net startup operation may still be using it.
        var discordClient = new DiscordSocketClient(DiscordSocketConfiguration.Create());
        services.AddSingleton(discordClient);

        services.AddSingleton<IValidateOptions<BeanBotSettings>, BeanBotSettingsValidator>();
        services.AddOptions<BeanBotSettings>()
            .Bind(configuration.GetSection(BeanBotSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton(provider => BeanBotOptionsFactory.Create(
            provider.GetRequiredService<IOptions<BeanBotSettings>>().Value));
        services.AddSingleton(_ => new CommandService(new CommandServiceConfig
        {
            LogLevel = LogSeverity.Verbose,
            CaseSensitiveCommands = false
        }));

        services.AddSingleton<MongoClient>(provider =>
            new MongoClient(provider.GetRequiredService<BeanBotOptions>().MongoConnectionString));
        services.AddSingleton<IMongoDatabase>(provider =>
            provider.GetRequiredService<MongoClient>().GetDatabase("BeanBotDB"));
        services.AddSingleton<IMongoReadinessProbe, MongoReadinessProbe>();
        services.AddSingleton<MongoReadinessMonitor>();

        services.AddSingleton<DiscordConnectionHealth>();
        services.AddSingleton<DiscordLifecycleCoordinator>();
        services.AddSingleton(DiscordStartupOptions.Default);
        services.AddSingleton<DiscordStartupLifecycle>(provider =>
        {
            var options = provider.GetRequiredService<DiscordStartupOptions>();
            return new DiscordStartupLifecycle(
                provider.GetRequiredService<DiscordSocketClient>(),
                provider.GetRequiredService<BeanBotOptions>().BotToken,
                options.LifecycleOperationTimeout,
                provider.GetRequiredService<DiscordLifecycleCoordinator>(),
                provider.GetRequiredService<ILogger<DiscordStartupLifecycle>>());
        });
        services.AddSingleton<IDiscordStartupLifecycle>(provider =>
            provider.GetRequiredService<DiscordStartupLifecycle>());
        services.AddSingleton<IDiscordStartupDelay, DiscordStartupDelay>();
        services.AddSingleton<DiscordStartupService>();

        services.AddSingleton(DiscordGatewayRecoveryOptions.Default);
        services.AddSingleton<IDiscordGatewayLifecycle>(provider =>
        {
            var options = provider.GetRequiredService<DiscordGatewayRecoveryOptions>();
            return new DiscordGatewayLifecycle(
                provider.GetRequiredService<DiscordSocketClient>(),
                provider.GetRequiredService<BeanBotOptions>().BotToken,
                options.LifecycleOperationTimeout,
                provider.GetRequiredService<DiscordLifecycleCoordinator>(),
                provider.GetRequiredService<ILogger<DiscordGatewayLifecycle>>());
        });
        services.AddSingleton<IRecoveryDelay, TaskRecoveryDelay>();

        services.AddSingleton<DiscordOutageStore>(provider => new DiscordOutageStore(
            Path.GetFullPath(DirectorySetup.botBaseDirectory),
            provider.GetRequiredService<ILogger<DiscordOutageStore>>()));
        services.AddSingleton<IDiscordOutageStore>(provider =>
            provider.GetRequiredService<DiscordOutageStore>());

        services.AddSingleton<DiscordOwnerAlertDelivery>();
        services.AddSingleton<IOwnerAlertDelivery>(provider =>
            provider.GetRequiredService<DiscordOwnerAlertDelivery>());
        services.AddSingleton<DiscordOwnerErrorNotifier>(provider =>
            new DiscordOwnerErrorNotifier(provider.GetRequiredService<IOwnerAlertDelivery>()));
        services.AddSingleton<IOwnerErrorNotifier>(provider =>
            provider.GetRequiredService<DiscordOwnerErrorNotifier>());
        services.AddSingleton<DiscordOutageRecoveryNotifier>();
        services.AddSingleton<LogHandler>();
        services.AddSingleton<DiscordLegacyCommandFeedbackDelivery>();
        services.AddSingleton<ILegacyCommandFeedbackDelivery>(provider =>
            provider.GetRequiredService<DiscordLegacyCommandFeedbackDelivery>());
        services.AddSingleton<LegacyCommandFeedbackResponder>();

        services.AddSingleton<DiscordGatewayRecoveryService>(provider =>
        {
            var client = provider.GetRequiredService<DiscordSocketClient>();
            var connectionHealth = provider.GetRequiredService<DiscordConnectionHealth>();
            return new DiscordGatewayRecoveryService(
                () => connectionHealth.CreateSnapshot(client),
                provider.GetRequiredService<IDiscordGatewayLifecycle>(),
                provider.GetRequiredService<IDiscordOutageStore>(),
                provider.GetRequiredService<ILogger<DiscordGatewayRecoveryService>>(),
                provider.GetRequiredService<DiscordGatewayRecoveryOptions>(),
                provider.GetRequiredService<IRecoveryDelay>());
        });

        services.AddSingleton<FortuneAnswerStore>();
        services.AddSingleton<PunProvider>();
        services.AddSingleton<IPunProvider>(provider =>
            provider.GetRequiredService<PunProvider>());
        services.AddSingleton(ExternalMediaCommandOptions.Default);
        services.AddSingleton<ExternalImageClient>();
        services.AddSingleton<IExternalImageClient>(provider =>
            provider.GetRequiredService<ExternalImageClient>());
        services.AddSingleton<MemeProvider>();
        services.AddSingleton<IMemeProvider>(provider =>
            provider.GetRequiredService<MemeProvider>());
        services.AddSingleton<DiscordMessageWaiter>();
        services.AddSingleton<DiscordPaginatorService>();
        services.AddSingleton<EditMessageEventServices>();
        services.AddSingleton<RoleReactRepository>();
        services.AddSingleton<RoleReactService>();
        services.AddSingleton<DiscordMessageCleanupService>();

        services.AddSingleton<CommandHandler>();
        services.AddSingleton<PunHandler>();
        services.AddSingleton<EditMessageHandler>();
        services.AddSingleton<NewMemberHandler>();
        services.AddSingleton<ReactHandler>();
        services.AddSingleton<HealthCheckServer>(provider => new HealthCheckServer(
            provider.GetRequiredService<BeanBotOptions>().HealthCheck,
            provider.GetRequiredService<DiscordSocketClient>(),
            provider.GetRequiredService<DiscordConnectionHealth>(),
            provider.GetRequiredService<MongoReadinessMonitor>(),
            provider.GetRequiredService<ILogger<HealthCheckServer>>()));

        services.AddSingleton<BeanBotRuntime>();
        services.AddSingleton<IBeanBotRuntime>(provider =>
            provider.GetRequiredService<BeanBotRuntime>());
        services.AddSingleton<BeanBotApplication>();
        services.AddSingleton<IBeanBotApplication>(provider =>
            provider.GetRequiredService<BeanBotApplication>());
        services.AddSingleton<BeanBotHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<BeanBotHostedService>());

        return services;
    }
}
