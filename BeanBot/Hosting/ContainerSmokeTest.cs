using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeanBot.Hosting;

internal static class ContainerSmokeTest
{
    internal const string Argument = "--container-smoke-test";
    internal const string ShutdownArgument = "--container-shutdown-smoke-test";
    internal const string ShutdownReadyMessage = "BeanBot container shutdown smoke test ready.";

    public static async Task<int> RunShutdownAsync(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = Program.HostShutdownTimeout);
        using var host = builder.Build();
        await host.StartAsync();
        output.WriteLine(ShutdownReadyMessage);
        await output.FlushAsync();
        await host.WaitForShutdownAsync();
        return 0;
    }

    public static int Run(
        string persistentDataDirectory,
        string punResourcePath,
        TextWriter output,
        TextWriter error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(punResourcePath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            LocalRuntimePrerequisites.Validate(
                persistentDataDirectory,
                punResourcePath,
                "container-smoke");

            output.WriteLine(
                $"BeanBot container smoke test passed. Version={BuildIdentity.Current.Version}, CommitSha={BuildIdentity.Current.CommitSha}");
            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine($"BeanBot container smoke test failed: {exception.Message}");
            return 1;
        }
    }
}
