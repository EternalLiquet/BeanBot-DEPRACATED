using BeanBot.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace BeanBot.Hosting;

internal static class DeploymentPreflight
{
    internal const string Argument = "--preflight";
    internal const int InvalidArgumentsExitCode = 2;

    internal static bool IsRequested(IReadOnlyCollection<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(Argument, StringComparer.Ordinal);
    }

    internal static int Run(
        IReadOnlyList<string> args,
        string persistentDataDirectory,
        string punResourcePath,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(punResourcePath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Count != 1 || !string.Equals(args[0], Argument, StringComparison.Ordinal))
        {
            error.WriteLine("BeanBot preflight failed: --preflight must be used by itself.");
            return InvalidArgumentsExitCode;
        }

        try
        {
            var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
            builder.Configuration.AddBeanBotConfiguration();
            return Run(
                builder.Configuration,
                persistentDataDirectory,
                punResourcePath,
                output,
                error);
        }
        catch (Exception)
        {
            error.WriteLine("BeanBot preflight failed: configuration could not be loaded safely.");
            return 1;
        }
    }

    internal static int Run(
        IConfiguration configuration,
        string persistentDataDirectory,
        string punResourcePath,
        TextWriter output,
        TextWriter error,
        Action<string>? afterProbeWritten = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(punResourcePath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        BeanBotOptions options;
        try
        {
            var services = new ServiceCollection();
            services.AddBeanBotOptions(configuration);
            using var provider = services.BuildServiceProvider();

            options = provider.GetRequiredService<BeanBotOptions>();
            _ = provider.GetRequiredService<NewMemberWelcomeOptions>();
        }
        catch (OptionsValidationException exception)
        {
            error.WriteLine("BeanBot preflight failed: configuration validation failed.");
            foreach (var failure in exception.Failures)
            {
                error.WriteLine($"- {failure}");
            }

            return 1;
        }
        catch (Exception)
        {
            error.WriteLine("BeanBot preflight failed: configuration could not be projected into runtime options.");
            return 1;
        }

        if (!IsMongoConnectionStringSyntacticallyValid(options.MongoConnectionString))
        {
            error.WriteLine(
                $"BeanBot preflight failed: invalid value for {BeanBotConfiguration.MongoConnectionVariable}. " +
                "Expected a syntactically valid MongoDB connection string.");
            return 1;
        }

        try
        {
            LocalRuntimePrerequisites.Validate(
                persistentDataDirectory,
                punResourcePath,
                "preflight",
                afterProbeWritten);
        }
        catch (LocalRuntimePrerequisiteException exception)
        {
            error.WriteLine($"BeanBot preflight failed: {exception.Message}");
            return 1;
        }

        output.WriteLine(
            $"BeanBot preflight passed. Version={BuildIdentity.Current.Version}, CommitSha={BuildIdentity.Current.CommitSha}");
        return 0;
    }

    private static bool IsMongoConnectionStringSyntacticallyValid(string connectionString)
    {
        try
        {
            _ = new MongoUrl(connectionString);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
