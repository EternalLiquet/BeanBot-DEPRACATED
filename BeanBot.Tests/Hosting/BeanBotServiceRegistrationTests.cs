using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.Lifecycle;
using BeanBot.Discord.ReactionRoles;
using BeanBot.Discord.RoleMenus;
using BeanBot.Hosting;
using BeanBot.Logging;
using BeanBot.Persistence.Outages;
using BeanBot.Persistence.Repositories;

using Discord.WebSocket;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotServiceRegistrationTests
{
    [Fact]
    public void AddBeanBot_RegistersOneHostedServiceAndHostOwnedDependencies()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();

        services.AddBeanBot(configuration);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        AssertSingleton<BeanBotOptions>(services);
        AssertSingleton<IValidateOptions<BeanBotSettings>>(services);
        AssertSingleton<IBeanBotRuntime>(services);
        AssertSingleton<IBeanBotApplication>(services);
        AssertSingleton<BeanBotHostedService>(services);
        AssertSingleton<RoleReactService>(services);
        AssertSingleton<RoleMenuRepository>(services);
        AssertSingleton<RoleMenuDraftRegistry>(services);
        AssertSingleton<RoleMenuMutationCoordinator>(services);
        AssertSingleton<PunProvider>(services);
        AssertSingleton<IPunProvider>(services);
        AssertSingleton<EditMessageEventServices>(services);
        AssertSingleton<LogHandler>(services);

        var clientDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(DiscordSocketClient));
        Assert.Equal(ServiceLifetime.Singleton, clientDescriptor.Lifetime);
        Assert.NotNull(clientDescriptor.ImplementationInstance);

        ((DiscordSocketClient)clientDescriptor.ImplementationInstance!).Dispose();
    }

    [Fact]
    public void AddBeanBotInteractions_RegistersOneHandlerAndRoleMenuFacade()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        services.AddBeanBot(configuration);

        services.AddBeanBotInteractions();

        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)));
        AssertSingleton<InteractionExecutionContext>(services);
        AssertSingleton<RoleMenuInteractionService>(services);
        AssertSingleton<InteractionHandler>(services);
        AssertSingleton<BeanBotInteractionHostedService>(services);

        var clientDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(DiscordSocketClient));
        ((DiscordSocketClient)clientDescriptor.ImplementationInstance!).Dispose();
    }

    [Fact]
    public async Task AddBeanBotInteractions_RegisteredGraphResolvesFromHostProvider()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration[BeanBotConfiguration.BotTokenVariable] = "test-token";
        builder.Configuration[BeanBotConfiguration.MongoConnectionVariable] =
            "mongodb://127.0.0.1:27017";
        builder.Configuration[BeanBotConfiguration.GeneralChannelVariable] = "123";
        builder.Configuration[BeanBotConfiguration.HatoeteUrlVariable] =
            "https://example.test/hatoete.png";
        builder.Configuration[BeanBotConfiguration.YoshimaruUrlVariable] =
            "https://example.test/yoshimaru.png";
        builder.Configuration.AddBeanBotConfiguration([]);
        builder.Services.AddBeanBot(builder.Configuration);
        builder.Services.AddBeanBotInteractions();
        var host = builder.Build();
        var discordClient = host.Services.GetRequiredService<DiscordSocketClient>();
        try
        {
            Assert.NotNull(host.Services.GetRequiredService<RoleMenuInteractionService>());
            Assert.NotNull(host.Services.GetRequiredService<InteractionHandler>());

            var startupLifecycle = host.Services.GetRequiredService<DiscordStartupLifecycle>();
            Assert.Same(
                startupLifecycle,
                host.Services.GetRequiredService<IDiscordStartupLifecycle>());
            var outageStore = host.Services.GetRequiredService<DiscordOutageStore>();
            Assert.Same(outageStore, host.Services.GetRequiredService<IDiscordOutageStore>());
            var ownerAlertDelivery = host.Services.GetRequiredService<DiscordOwnerAlertDelivery>();
            Assert.Same(
                ownerAlertDelivery,
                host.Services.GetRequiredService<IOwnerAlertDelivery>());
            var ownerErrorNotifier = host.Services.GetRequiredService<DiscordOwnerErrorNotifier>();
            Assert.Same(
                ownerErrorNotifier,
                host.Services.GetRequiredService<IOwnerErrorNotifier>());
            Assert.NotNull(host.Services.GetRequiredService<DiscordGatewayRecoveryService>());
            Assert.Same(
                host.Services.GetRequiredService<PunProvider>(),
                host.Services.GetRequiredService<IPunProvider>());

            var hostedServices = host.Services.GetServices<IHostedService>().ToList();
            Assert.Equal(2, hostedServices.Count);
            Assert.Contains(hostedServices, service => service is BeanBotHostedService);
            Assert.Same(
                host.Services.GetRequiredService<BeanBotInteractionHostedService>(),
                Assert.Single(hostedServices.OfType<BeanBotInteractionHostedService>()));
        }
        finally
        {
            try
            {
                await ((IAsyncDisposable)host).DisposeAsync();
            }
            finally
            {
                discordClient.Dispose();
            }
        }
    }

    private static void AssertSingleton<TService>(IEnumerable<ServiceDescriptor> services)
    {
        var descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}
