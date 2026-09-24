using System.Globalization;
using System.Net;
using System.Net.Sockets;
using BeanBot.Configuration;
using BeanBot.Health;
using BeanBot.Hosting;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class DeploymentPreflightTests
{
    [Fact]
    public void Run_ValidProductionStyleConfiguration_ReturnsSuccessAndCleansProbe()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(temporaryDirectory, "BeanBotFiles");
        var resourcePath = CreatePunResource(temporaryDirectory);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = DeploymentPreflight.Run(
                CreateConfiguration(RequiredSettings()),
                dataDirectory,
                resourcePath,
                output,
                error);

            Assert.Equal(0, result);
            Assert.Contains("BeanBot preflight passed.", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Version=0.0.0-local", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
            Assert.Empty(Directory.EnumerateFiles(dataDirectory));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void Run_MissingRequiredConfiguration_UsesProductionValidator()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var resourcePath = CreatePunResource(temporaryDirectory);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = DeploymentPreflight.Run(
                CreateConfiguration(new Dictionary<string, string?>()),
                Path.Combine(temporaryDirectory, "BeanBotFiles"),
                resourcePath,
                output,
                error);

            Assert.Equal(1, result);
            Assert.Empty(output.ToString());
            Assert.Contains("configuration validation failed", error.ToString(), StringComparison.Ordinal);
            Assert.Contains(BeanBotConfiguration.BotTokenVariable, error.ToString(), StringComparison.Ordinal);
            Assert.Contains(BeanBotConfiguration.MongoConnectionVariable, error.ToString(), StringComparison.Ordinal);
            Assert.Contains(BeanBotConfiguration.GeneralChannelVariable, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("BEANBOT_GENERAL_CHANNEL_ID", "not-a-snowflake")]
    [InlineData("BEANBOT_HATOETE_URL", "relative/image.png")]
    [InlineData("BEANBOT_DAILY_PUN_TIME", "25:61")]
    [InlineData("BEANBOT_DAILY_PUN_TIMEZONE", "Mars/Olympus_Mons")]
    [InlineData("BEANBOT_HEALTHCHECK_PORT", "70000")]
    public void Run_MalformedProductionSetting_IsRejectedConsistently(string key, string value)
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var resourcePath = CreatePunResource(temporaryDirectory);
        var values = RequiredSettings();
        values[key] = value;
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = DeploymentPreflight.Run(
                CreateConfiguration(values),
                Path.Combine(temporaryDirectory, "BeanBotFiles"),
                resourcePath,
                output,
                error);

            Assert.Equal(1, result);
            Assert.Empty(output.ToString());
            Assert.Contains(key, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void Run_MalformedMongoConnectionString_IsRejectedWithoutLeakingIt()
    {
        const string malformedMongoConnectionString =
            "mongodb://preflight-user:super-secret-marker@[::1";
        var temporaryDirectory = CreateTemporaryDirectory();
        var resourcePath = CreatePunResource(temporaryDirectory);
        var values = RequiredSettings();
        values[BeanBotConfiguration.MongoConnectionVariable] = malformedMongoConnectionString;
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = DeploymentPreflight.Run(
                CreateConfiguration(values),
                Path.Combine(temporaryDirectory, "BeanBotFiles"),
                resourcePath,
                output,
                error);

            Assert.Equal(1, result);
            Assert.Empty(output.ToString());
            Assert.Contains(BeanBotConfiguration.MongoConnectionVariable, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(malformedMongoConnectionString, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-marker", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_MissingOrEmptyPunResource_ReturnsFailure(bool createEmptyResource)
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var resourcePath = Path.Combine(temporaryDirectory, "puns.csv");
        if (createEmptyResource)
        {
            File.WriteAllText(resourcePath, string.Empty);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = DeploymentPreflight.Run(
                CreateConfiguration(RequiredSettings()),
                Path.Combine(temporaryDirectory, "BeanBotFiles"),
                resourcePath,
                output,
                error);

            Assert.Equal(1, result);
            Assert.Empty(output.ToString());
            Assert.Contains("missing or empty", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(temporaryDirectory, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void Run_UncreatablePersistentDataDirectory_ReturnsSanitizedFailure()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var resourcePath = CreatePunResource(temporaryDirectory);
        var blockingFile = Path.Combine(temporaryDirectory, "not-a-directory");
        File.WriteAllText(blockingFile, "blocking file");
        var dataDirectory = Path.Combine(blockingFile, "BeanBotFiles");
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = DeploymentPreflight.Run(
                CreateConfiguration(RequiredSettings()),
                dataDirectory,
                resourcePath,
                output,
                error);

            Assert.Equal(1, result);
            Assert.Empty(output.ToString());
            Assert.Contains("could not be created or accessed", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(temporaryDirectory, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void Run_FailureAfterProbeCreation_CleansTemporaryProbe()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(temporaryDirectory, "BeanBotFiles");
        var resourcePath = CreatePunResource(temporaryDirectory);
        string? probePath = null;
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = DeploymentPreflight.Run(
                CreateConfiguration(RequiredSettings()),
                dataDirectory,
                resourcePath,
                output,
                error,
                path =>
                {
                    probePath = path;
                    throw new IOException("synthetic post-probe failure");
                });

            Assert.Equal(1, result);
            Assert.NotNull(probePath);
            Assert.False(File.Exists(probePath));
            Assert.Empty(Directory.EnumerateFiles(dataDirectory));
            Assert.DoesNotContain("synthetic post-probe failure", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void Run_OfflineInputs_DoNotRequireExternalDependenciesOrHealthPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var temporaryDirectory = CreateTemporaryDirectory();
        var resourcePath = CreatePunResource(temporaryDirectory);
        var values = RequiredSettings();
        values[BeanBotConfiguration.BotTokenVariable] = "offline-discord-token-marker";
        values[BeanBotConfiguration.MongoConnectionVariable] =
            "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=1";
        values[BeanBotConfiguration.HatoeteUrlVariable] = "https://offline-preflight.invalid/hatoete.png";
        values[BeanBotConfiguration.YoshimaruUrlVariable] = "https://offline-preflight.invalid/yoshimaru.png";
        values[BeanBotConfiguration.HealthCheckPortVariable] = port.ToString(CultureInfo.InvariantCulture);
        values[BeanBotConfiguration.HealthCheckBindAddressVariable] = "127.0.0.1";
        values[BeanBotConfiguration.HealthCheckBearerTokenVariable] = "offline-health-secret-marker";
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = DeploymentPreflight.Run(
                CreateConfiguration(values),
                Path.Combine(temporaryDirectory, "BeanBotFiles"),
                resourcePath,
                output,
                error);

            Assert.Equal(0, result);
            Assert.Empty(error.ToString());
            Assert.DoesNotContain("offline-discord-token-marker", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("offline-health-secret-marker", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void OptionsOnlyRegistration_DoesNotRegisterRuntimeOrHostedServices()
    {
        var services = new ServiceCollection();
        services.AddBeanBotOptions(CreateConfiguration(RequiredSettings()));

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(DiscordSocketClient));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(MongoClient));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(HealthCheckServer));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Run_ValidationFailure_DoesNotExposeConfiguredSecrets()
    {
        const string botSecret = "preflight-bot-secret-marker";
        const string mongoSecret = "mongodb://user:preflight-mongo-secret-marker@localhost:27017";
        const string healthSecret = "preflight-health-secret-marker";
        var temporaryDirectory = CreateTemporaryDirectory();
        var resourcePath = CreatePunResource(temporaryDirectory);
        var values = RequiredSettings();
        values[BeanBotConfiguration.BotTokenVariable] = botSecret;
        values[BeanBotConfiguration.MongoConnectionVariable] = mongoSecret;
        values[BeanBotConfiguration.HealthCheckPortVariable] = "8080";
        values[BeanBotConfiguration.HealthCheckBearerTokenVariable] = healthSecret;
        values[BeanBotConfiguration.HatoeteUrlVariable] = "invalid";
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = DeploymentPreflight.Run(
                CreateConfiguration(values),
                Path.Combine(temporaryDirectory, "BeanBotFiles"),
                resourcePath,
                output,
                error);

            var diagnostics = output.ToString() + error.ToString();
            Assert.Equal(1, result);
            Assert.DoesNotContain(botSecret, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(mongoSecret, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(healthSecret, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void Run_MixedPreflightArguments_FailsBeforeConfigurationOrStartup()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = DeploymentPreflight.Run(
            [DeploymentPreflight.Argument, ContainerSmokeTest.Argument],
            "unused-data-directory",
            "unused-resource",
            output,
            error);

        Assert.Equal(DeploymentPreflight.InvalidArgumentsExitCode, result);
        Assert.Empty(output.ToString());
        Assert.Contains("must be used by itself", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--preflight", true)]
    [InlineData("--container-smoke-test", false)]
    [InlineData("--anything-else", false)]
    public void IsRequested_DetectsOnlyPreflightArgument(string argument, bool expected)
    {
        Assert.Equal(expected, DeploymentPreflight.IsRequested([argument]));
    }

    private static ConfigurationManager CreateConfiguration(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        configuration.AddBeanBotConfiguration(Array.Empty<string>());
        return configuration;
    }

    private static Dictionary<string, string?> RequiredSettings() => new()
    {
        [BeanBotConfiguration.BotTokenVariable] = "token",
        [BeanBotConfiguration.MongoConnectionVariable] = "mongodb://localhost:27017",
        [BeanBotConfiguration.GeneralChannelVariable] = "123",
        [BeanBotConfiguration.HatoeteUrlVariable] = "https://example.com/hatoete.png",
        [BeanBotConfiguration.YoshimaruUrlVariable] = "https://example.com/yoshimaru.png"
    };

    private static string CreatePunResource(string temporaryDirectory)
    {
        var resourcePath = Path.Combine(temporaryDirectory, "puns.csv");
        File.WriteAllText(resourcePath, "BadPost\nA test pun");
        return resourcePath;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"BeanBotPreflightTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
