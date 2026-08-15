using BeanBot.Configuration;
using BeanBot.Hosting;
using BeanBot.Services;

using Discord.WebSocket;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace BeanBot.Tests.Hosting;

public class BeanBotServiceRegistrationTests
{
    [Fact]
    public void AddBeanBot_RegistersOneHostedServiceAndHostOwnedDependencies()
    {
        var services = new ServiceCollection();

        services.AddBeanBot();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        AssertSingleton<BeanBotOptions>(services);
        AssertSingleton<IBeanBotRuntime>(services);
        AssertSingleton<IBeanBotApplication>(services);
        AssertSingleton<BeanBotHostedService>(services);
        AssertSingleton<RoleReactService>(services);

        var clientDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(DiscordSocketClient));
        Assert.Equal(ServiceLifetime.Singleton, clientDescriptor.Lifetime);
        Assert.NotNull(clientDescriptor.ImplementationInstance);

        ((DiscordSocketClient)clientDescriptor.ImplementationInstance!).Dispose();
    }

    private static void AssertSingleton<TService>(IEnumerable<ServiceDescriptor> services)
    {
        var descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}
