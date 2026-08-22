using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.ReactionRoles;
using BeanBot.Discord.RoleMenus;
using BeanBot.Hosting;
using BeanBot.Logging;
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
        AssertSingleton<RoleMenuMemberSynchronizer>(services);
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
        builder.Services.AddBeanBot(builder.Configuration);
        builder.Services.AddBeanBotInteractions();
        await using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<RoleMenuInteractionService>());
        Assert.NotNull(host.Services.GetRequiredService<InteractionHandler>());

        host.Services.GetRequiredService<DiscordSocketClient>().Dispose();
    }

    private static void AssertSingleton<TService>(IEnumerable<ServiceDescriptor> services)
    {
        var descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}
