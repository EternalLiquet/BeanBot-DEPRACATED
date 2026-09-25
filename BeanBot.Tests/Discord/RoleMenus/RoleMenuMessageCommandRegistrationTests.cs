using BeanBot.Discord.Interactions;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public sealed class RoleMenuMessageCommandRegistrationTests
{
    [Fact]
    public async Task InteractionService_DiscoversMessageCommandForGlobalRegistration()
    {
        using var client = new DiscordRestClient();
        using var repository = new RoleMenuRepository(
            new MongoClient("mongodb://localhost:27017").GetDatabase("registration_test"),
            NullLogger<RoleMenuRepository>.Instance);
        var roleMenus = new RoleMenuInteractionService(
            repository,
            new RoleMenuDraftRegistry(),
            new RoleMenuMutationCoordinator(),
            new InteractionExecutionContext());
        using var services = new ServiceCollection()
            .AddSingleton(roleMenus)
            .BuildServiceProvider();
        var interactions = new InteractionService(client, new InteractionServiceConfig());

        await interactions.AddModuleAsync<RoleMenuMessageCommandModule>(services);

        var command = Assert.Single(interactions.ContextCommands,
            candidate => candidate.Name == "Delete Role Menu");
        Assert.Equal(ApplicationCommandType.Message, command.CommandType);
        Assert.Equal(typeof(RoleMenuMessageCommandModule).Name, command.Module.Name);
    }
}
