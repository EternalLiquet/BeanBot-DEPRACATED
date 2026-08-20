using BeanBot.Configuration;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Events;
using BeanBot.Discord.Lifecycle;
using BeanBot.Discord.Messaging;
using BeanBot.Discord.ReactionRoles;
using BeanBot.Health;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Outages;
using BeanBot.Persistence.Repositories;
using Xunit;

namespace BeanBot.Tests.Architecture;

public class ResponsibilityNamespaceTests
{
    private static readonly HashSet<string> RetiredNamespaces =
    [
        "BeanBot.Attributes",
        "BeanBot.Entities",
        "BeanBot.EventHandlers",
        "BeanBot.Modules",
        "BeanBot.Repository",
        "BeanBot.Services",
        "BeanBot.Util"
    ];

    [Fact]
    public void ProductionTypes_UseResponsibilityNamespaces()
    {
        var productionTypes = typeof(BeanBotOptions).Assembly.GetTypes();

        Assert.DoesNotContain(
            productionTypes,
            type => type.Namespace is not null && RetiredNamespaces.Contains(type.Namespace));

        var representativeTypes = new (Type Type, string ExpectedNamespace)[]
        {
            (typeof(AdministrativeModule), "BeanBot.Discord.Commands"),
            (typeof(CommandHandler), "BeanBot.Discord.Events"),
            (typeof(DiscordStartupService), "BeanBot.Discord.Lifecycle"),
            (typeof(DiscordPaginatorService), "BeanBot.Discord.Messaging"),
            (typeof(RoleReactService), "BeanBot.Discord.ReactionRoles"),
            (typeof(HealthCheckServer), "BeanBot.Health"),
            (typeof(LogHandler), "BeanBot.Logging"),
            (typeof(RoleSettings), "BeanBot.Persistence.Models"),
            (typeof(DiscordOutageStore), "BeanBot.Persistence.Outages"),
            (typeof(RoleReactRepository), "BeanBot.Persistence.Repositories")
        };

        foreach (var (type, expectedNamespace) in representativeTypes)
        {
            Assert.Equal(expectedNamespace, type.Namespace);
        }
    }
}
