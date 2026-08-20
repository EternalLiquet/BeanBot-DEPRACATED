using BeanBot.Configuration;
using BeanBot.Discord.Interactions;
using BeanBot.Hosting;
using BeanBot.Logging;
using BeanBot.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace BeanBot;

internal static class Program
{
    internal static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromMinutes(2);

    private static async Task<int> Main(string[] args)
    {
        IHost? host = null;
        Log.Logger = LogHandler.CreateBootstrapLogger();
        try
        {
            DirectorySetup.MakeSureAllDirectoriesExist();

            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddBeanBotConfiguration();
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options =>
                options.ShutdownTimeout = HostShutdownTimeout);
            builder.Services.AddBeanBot(builder.Configuration);
            builder.Services.AddBeanBotInteractions();
            builder.Services.AddSerilog((services, loggerConfiguration) =>
                LogHandler.ConfigureLogger(
                    loggerConfiguration,
                    services.GetRequiredService<DiscordOwnerErrorNotifier>()));

            host = builder.Build();

            await host.RunAsync();
            return 0;
        }
        catch (OperationCanceledException) when (IsHostStopping(host))
        {
            Log.Warning("Shutting down");
            return 0;
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "BeanBot terminated because of an unhandled exception");
            return 1;
        }
        finally
        {
            try
            {
                if (host is not null)
                {
                    if (host is IAsyncDisposable asyncDisposableHost)
                    {
                        await asyncDisposableHost.DisposeAsync();
                    }
                    else
                    {
                        host.Dispose();
                    }
                }
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        }
    }

    private static bool IsHostStopping(IHost? host)
        => host?.Services
            .GetService<IHostApplicationLifetime>()
            ?.ApplicationStopping
            .IsCancellationRequested == true;
}
