using BeanBot.Configuration;
using BeanBot.Repository;
using BeanBot.Services;
using BeanBot.Util;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.Commands;
using NetCord.Services.Commands;
using Serilog;
using Serilog.Events;
using System.Reflection;

namespace BeanBot;

public static class Program
{
    private const string ExternalMediaHttpClientName = "ExternalMedia";
    private static readonly Uri MemeApiBaseAddress = new("https://meme-api.com/");

    public static async Task<int> Main(string[] args)
    {
        try
        {
            DotEnvLoader.LoadIfPresent();
            DirectorySetup.EnsureDirectoriesExist();

            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddEnvironmentVariables();

            builder.Services.AddSingleton<IConfigureOptions<BeanBotOptions>, BeanBotOptionsSetup>();
            builder.Services.AddSingleton<IValidateOptions<BeanBotOptions>, BeanBotOptionsValidator>();
            builder.Services.AddOptions<BeanBotOptions>().ValidateOnStart();

            builder.Services.AddSerilog((_, loggerConfiguration) =>
            {
                var configuredLogLevel = builder.Configuration["BEANBOT_LOG_LEVEL"]
                    ?? builder.Configuration["logLevel"]
                    ?? "Information";
                var minimumLevel = ResolveLogLevel(configuredLogLevel);

                loggerConfiguration
                    .MinimumLevel.Is(minimumLevel)
                    .Enrich.FromLogContext()
                    .WriteTo.Console()
                    .WriteTo.Async(sink => sink.File(Path.Combine(DirectorySetup.LogsDirectory, "BeanBotLogs-.txt"), rollingInterval: RollingInterval.Day));
            });

            builder.Services.AddHttpClient(ExternalMediaHttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BeanBot/2.0");
            });
            builder.Services.AddHttpClient<IMemeService, MemeService>(client =>
            {
                client.BaseAddress = MemeApiBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BeanBot/2.0");
            });
            builder.Services.AddSingleton<IMongoClient>(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<BeanBotOptions>>().Value;
                return new MongoClient(MongoUrl.Create(options.MongoConnectionString));
            });
            builder.Services.AddSingleton(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<BeanBotOptions>>().Value;
                var mongoUrl = MongoUrl.Create(options.MongoConnectionString);
                var databaseName = string.IsNullOrWhiteSpace(mongoUrl.DatabaseName) ? "BeanBotDB" : mongoUrl.DatabaseName;
                return serviceProvider.GetRequiredService<IMongoClient>().GetDatabase(databaseName);
            });

            builder.Services.AddSingleton<DiscordReadySignal>();
            builder.Services.AddSingleton<GuildUserResolverService>();
            builder.Services.AddSingleton<HelpCatalogService>();
            builder.Services.AddSingleton<MessagePromptService>();
            builder.Services.AddSingleton<EightBallQueueService>();
            builder.Services.AddSingleton<GuildRoleManagementService>();
            builder.Services.AddSingleton<IPunCatalogService, PunCatalogService>();
            builder.Services.AddSingleton<IRoleReactRepository, RoleReactRepository>();
            builder.Services.AddSingleton<RoleReactService>();
            builder.Services.AddHostedService<EventHandlers.PunHandler>();

            builder.Services.AddDiscordGateway((options, serviceProvider) =>
            {
                var botOptions = serviceProvider.GetRequiredService<IOptions<BeanBotOptions>>().Value;

                options.Token = botOptions.BotToken;
                options.Intents = GatewayIntents.Guilds
                    | GatewayIntents.GuildUsers
                    | GatewayIntents.GuildMessages
                    | GatewayIntents.GuildMessageReactions
                    | GatewayIntents.DirectMessages
                    | GatewayIntents.MessageContent;
                options.Presence = new PresenceProperties(UserStatusType.Online)
                {
                    Activities =
                    [
                        new UserActivityProperties("My purpose is to bully Hatate and succ the world dry", UserActivityType.Playing),
                    ],
                };
            });

            builder.Services.AddGatewayHandlers(Assembly.GetExecutingAssembly());
            builder.Services.AddCommands(options =>
            {
                options.GetCommandTextAsync = static (_, _, _) => new ValueTask<ReadOnlyMemory<char>?>(result: null);
            });

            using var host = builder.Build();
            var commandService = host.Services.GetRequiredService<CommandService<CommandContext>>();
            commandService.AddModules(Assembly.GetExecutingAssembly());

            await host.RunAsync();
            return 0;
        }
        catch (OptionsValidationException exception)
        {
            Console.Error.WriteLine("BeanBot configuration is invalid:");
            foreach (var failure in exception.Failures)
            {
                Console.Error.WriteLine($"- {failure}");
            }

            return 1;
        }
    }

    private static LogEventLevel ResolveLogLevel(string configuredLogLevel)
    {
        return Enum.TryParse(configuredLogLevel, true, out LogEventLevel level)
            ? level
            : LogEventLevel.Information;
    }
}

