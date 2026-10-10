using System.Reflection;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public sealed class RoleMenuCommandRegistrationTests
{
    [Fact]
    public async Task GlobalRegistration_IncludesGuildOnlyDeleteRoleMenuMessageCommand()
    {
        await using var registration = await RegistrationFixture.CreateAsync();

        var command = Assert.Single(
            registration.Interactions.ContextCommands,
            candidate => candidate.Name == RoleMenuMessageCommandModule.DeleteCommandName);
        Assert.Equal(ApplicationCommandType.Message, command.CommandType);
        Assert.False(command.Module.DontAutoRegister);
        Assert.Null(command.Module.Parent);

        var properties = Assert.IsType<MessageCommandProperties>(Assert.Single(
            registration.GetRegisteredProperties(),
            candidate => candidate is MessageCommandProperties
                         && candidate.Name.GetValueOrDefault() == "Delete Role Menu"));
        Assert.Equal(GuildPermission.ManageRoles, properties.DefaultMemberPermissions.GetValueOrDefault());
        Assert.Equal(
            [InteractionContextType.Guild],
            properties.ContextTypes.GetValueOrDefault()!);
    }

    [Fact]
    public async Task GlobalRegistration_DeleteSlashCommandTakesNoMenuId()
    {
        await using var registration = await RegistrationFixture.CreateAsync();

        var roleMenu = Assert.IsType<SlashCommandProperties>(Assert.Single(
            registration.GetRegisteredProperties(),
            candidate => candidate is SlashCommandProperties
                         && candidate.Name.GetValueOrDefault() == "role-menu"));
        var delete = Assert.Single(
            roleMenu.Options.GetValueOrDefault()!,
            option => option.Name == "delete");

        Assert.True(delete.Options is null or { Count: 0 });
        Assert.DoesNotContain("ID", delete.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageCommandModule_IsDiscoverableByAssemblyScanning()
    {
        var module = typeof(RoleMenuMessageCommandModule);

        Assert.True(module.IsPublic);
        Assert.False(module.IsAbstract);
        Assert.False(module.IsNested);
        Assert.Same(typeof(RoleMenuAdminModule).Assembly, module.Assembly);
        Assert.True(typeof(IInteractionModuleBase).IsAssignableFrom(module));
    }

    private sealed class RegistrationFixture : IAsyncDisposable
    {
        private readonly DiscordRestClient _client;
        private readonly ServiceProvider _services;

        private RegistrationFixture(
            DiscordRestClient client,
            ServiceProvider services,
            InteractionService interactions)
        {
            _client = client;
            _services = services;
            Interactions = interactions;
        }

        internal InteractionService Interactions { get; }

        internal static async Task<RegistrationFixture> CreateAsync()
        {
            var client = new DiscordRestClient();
            var roleMenus = new RoleMenuInteractionService(
                new RoleMenuRepository(
                    new MongoClient("mongodb://127.0.0.1:1").GetDatabase("registration_test"),
                    NullLogger<RoleMenuRepository>.Instance),
                new RoleMenuDraftRegistry(),
                new RoleMenuMutationCoordinator(),
                new InteractionExecutionContext());
            var discord = new DiscordRoleMenuClient(
                (_, _, _) => Task.FromResult<IGuildUser?>(null),
                (_, _) => Task.FromResult<IChannel?>(null));
            var services = new ServiceCollection()
                .AddSingleton(roleMenus)
                .AddSingleton(discord)
                .AddSingleton(new RoleMenuAuditService(roleMenus, discord))
                .AddSingleton(new RoleMenuAdministrationService(
                    roleMenus,
                    discord,
                    NullLogger<RoleMenuAdministrationService>.Instance))
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider();
            var interactions = new InteractionService(client, new InteractionServiceConfig());
            await interactions.AddModuleAsync<RoleMenuAdminModule>(services);
            await interactions.AddModuleAsync<RoleMenuMessageCommandModule>(services);
            return new RegistrationFixture(client, services, interactions);
        }

        /// <summary>
        /// Builds the same command payloads that global registration sends to Discord.
        /// </summary>
        internal IReadOnlyList<ApplicationCommandProperties> GetRegisteredProperties()
        {
            var toProperties = typeof(InteractionService).Assembly
                .GetType("Discord.Interactions.ApplicationCommandRestUtil", throwOnError: true)!
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(method => method.Name == "ToApplicationCommandProps"
                                  && method.GetParameters() is
                                      [{ ParameterType: var module }, { ParameterType: var flag }]
                                  && module == typeof(ModuleInfo)
                                  && flag == typeof(bool));
            return [.. Interactions.Modules
                .Where(module => module.Parent is null && !module.DontAutoRegister)
                .SelectMany(module => (IEnumerable<ApplicationCommandProperties>)
                    toProperties.Invoke(null, [module, false])!)];
        }

        public async ValueTask DisposeAsync()
        {
            Interactions.Dispose();
            await _services.DisposeAsync();
            _client.Dispose();
        }
    }
}
