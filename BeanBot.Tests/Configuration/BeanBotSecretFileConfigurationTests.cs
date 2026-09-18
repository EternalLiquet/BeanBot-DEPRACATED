using System.Globalization;
using System.Text;
using BeanBot.Configuration;
using BeanBot.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BeanBot.Tests.Configuration;

public class BeanBotSecretFileConfigurationTests
{
    [Fact]
    public void Options_LoadsBotTokenFromSecretFile()
    {
        using var secretFile = new TemporarySecretFile("file-backed-token");
        var values = RequiredSettings();
        values.Remove(BeanBotConfiguration.BotTokenVariable);
        values[BeanBotConfiguration.BotTokenFileVariable] = secretFile.FilePath;

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal("file-backed-token", options.BotToken);
    }

    [Fact]
    public void Options_LoadsMongoConnectionStringFromSecretFile()
    {
        using var secretFile = new TemporarySecretFile("mongodb://file-backed:27017");
        var values = RequiredSettings();
        values.Remove(BeanBotConfiguration.MongoConnectionVariable);
        values[BeanBotConfiguration.MongoConnectionFileVariable] = secretFile.FilePath;

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal("mongodb://file-backed:27017", options.MongoConnectionString);
    }

    [Fact]
    public void Options_LoadsHealthBearerTokenFromSecretFileWhenHealthIsEnabled()
    {
        using var secretFile = new TemporarySecretFile("health-file-token");
        var values = RequiredSettings();
        values[BeanBotConfiguration.HealthCheckPortVariable] = "8080";
        values[BeanBotConfiguration.HealthCheckBearerTokenFileVariable] = secretFile.FilePath;

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal("health-file-token", options.HealthCheck.BearerToken);
    }

    [Theory]
    [InlineData("\n", "secret-with-space ")]
    [InlineData("\r\n", "secret-with-space ")]
    [InlineData("\n\n", "secret-with-space \n")]
    public void SecretFile_RemovesOnlyOneTerminalLineEnding(string lineEnding, string expected)
    {
        using var secretFile = new TemporarySecretFile($"secret-with-space {lineEnding}");
        var values = RequiredSettings();
        values.Remove(BeanBotConfiguration.BotTokenVariable);
        values[BeanBotConfiguration.BotTokenFileVariable] = secretFile.FilePath;

        using var provider = CreateProvider(values);
        var options = provider.GetRequiredService<BeanBotOptions>();

        Assert.Equal(expected, options.BotToken);
    }

    [Fact]
    public void SecretFile_RejectsCanonicalDirectValueConflictWithoutEchoingSecret()
    {
        const string secret = "canonical-secret-marker";
        using var secretFile = new TemporarySecretFile("file-secret-marker");
        var values = RequiredSettings();
        values[BeanBotConfiguration.BotTokenVariable] = secret;
        values[BeanBotConfiguration.BotTokenFileVariable] = secretFile.FilePath;

        var exception = Assert.Throws<InvalidOperationException>(() => CreateProvider(values));

        Assert.Contains(BeanBotConfiguration.BotTokenVariable, exception.Message);
        Assert.Contains(BeanBotConfiguration.BotTokenFileVariable, exception.Message);
        Assert.DoesNotContain(secret, exception.ToString());
        Assert.DoesNotContain("file-secret-marker", exception.ToString());
    }

    [Fact]
    public void SecretFile_RejectsLegacyDirectValueConflict()
    {
        using var secretFile = new TemporarySecretFile("file-secret");
        var values = RequiredSettings();
        values.Remove(BeanBotConfiguration.BotTokenVariable);
        values["botToken"] = "legacy-secret";
        values[BeanBotConfiguration.BotTokenFileVariable] = secretFile.FilePath;

        var exception = Assert.Throws<InvalidOperationException>(() => CreateProvider(values));

        Assert.Contains("botToken", exception.Message);
        Assert.Contains(BeanBotConfiguration.BotTokenFileVariable, exception.Message);
        Assert.DoesNotContain("legacy-secret", exception.ToString());
        Assert.DoesNotContain("file-secret", exception.ToString());
    }

    [Fact]
    public void SecretFile_RejectsMissingFileWithSanitizedError()
    {
        using var secretFile = new TemporarySecretFile("unused");
        File.Delete(secretFile.FilePath);
        var values = RequiredSettings();
        values.Remove(BeanBotConfiguration.BotTokenVariable);
        values[BeanBotConfiguration.BotTokenFileVariable] = secretFile.FilePath;

        var exception = Assert.Throws<InvalidOperationException>(() => CreateProvider(values));

        Assert.Contains(BeanBotConfiguration.BotTokenFileVariable, exception.Message);
        Assert.Contains("exists and is readable", exception.Message);
    }

    [Fact]
    public void SecretFile_RejectsDirectoryPath()
    {
        using var secretFile = new TemporarySecretFile("unused");
        var values = RequiredSettings();
        values.Remove(BeanBotConfiguration.BotTokenVariable);
        values[BeanBotConfiguration.BotTokenFileVariable] = secretFile.DirectoryPath;

        var exception = Assert.Throws<InvalidOperationException>(() => CreateProvider(values));

        Assert.Contains(BeanBotConfiguration.BotTokenFileVariable, exception.Message);
        Assert.Contains("exists and is readable", exception.Message);
    }

    [Fact]
    public void SecretFile_RejectsEmptyFile()
    {
        using var secretFile = new TemporarySecretFile(string.Empty);
        var values = RequiredSettings();
        values.Remove(BeanBotConfiguration.BotTokenVariable);
        values[BeanBotConfiguration.BotTokenFileVariable] = secretFile.FilePath;

        var exception = Assert.Throws<InvalidOperationException>(() => CreateProvider(values));

        Assert.Contains(BeanBotConfiguration.BotTokenFileVariable, exception.Message);
        Assert.Contains("must not be empty", exception.Message);
    }

    [Fact]
    public void SecretFile_RejectsOversizedFileWithoutReadingUnboundedContent()
    {
        using var secretFile = new TemporarySecretFile(
            new string('x', BeanBotConfiguration.SecretFileMaxBytes + 1));
        var values = RequiredSettings();
        values.Remove(BeanBotConfiguration.BotTokenVariable);
        values[BeanBotConfiguration.BotTokenFileVariable] = secretFile.FilePath;

        var exception = Assert.Throws<InvalidOperationException>(() => CreateProvider(values));

        Assert.Contains(BeanBotConfiguration.BotTokenFileVariable, exception.Message);
        Assert.Contains(
            BeanBotConfiguration.SecretFileMaxBytes.ToString(CultureInfo.InvariantCulture),
            exception.Message);
    }

    [Fact]
    public void SecretFile_InvalidContentErrorDoesNotEchoResolvedSecret()
    {
        const string secret = "resolved-secret-sentinel";
        using var secretFile = new TemporarySecretFile(secret + "\0");
        var values = RequiredSettings();
        values.Remove(BeanBotConfiguration.BotTokenVariable);
        values[BeanBotConfiguration.BotTokenFileVariable] = secretFile.FilePath;

        var exception = Assert.Throws<InvalidOperationException>(() => CreateProvider(values));

        Assert.Contains(BeanBotConfiguration.BotTokenFileVariable, exception.Message);
        Assert.DoesNotContain(secret, exception.ToString());
    }

    [Fact]
    public void DotEnv_LoadsCanonicalSecretFilePathWithoutChangingOtherDefaults()
    {
        using var secretFile = new TemporarySecretFile("dotenv-file-token\n");
        var dotEnvDirectory = Directory.CreateTempSubdirectory("beanbot-secret-dotenv-");
        try
        {
            var dotEnvPath = Path.Combine(dotEnvDirectory.FullName, ".env");
            File.WriteAllLines(dotEnvPath,
            [
                $"{BeanBotConfiguration.BotTokenFileVariable}={secretFile.FilePath}",
                $"{BeanBotConfiguration.MongoConnectionVariable}=mongodb://dotenv:27017",
                $"{BeanBotConfiguration.GeneralChannelVariable}=789",
                $"{BeanBotConfiguration.HatoeteUrlVariable}=https://dotenv.example/hatoete.png",
                $"{BeanBotConfiguration.YoshimaruUrlVariable}=https://dotenv.example/yoshimaru.png"
            ]);

            var configuration = new ConfigurationManager();
            configuration.AddBeanBotConfiguration([dotEnvPath]);
            using var provider = CreateProvider(configuration);
            var options = provider.GetRequiredService<BeanBotOptions>();

            Assert.Equal("dotenv-file-token", options.BotToken);
            Assert.Equal("mongodb://dotenv:27017", options.MongoConnectionString);
            Assert.Equal((ulong)789, options.GeneralChannelId);
        }
        finally
        {
            dotEnvDirectory.Delete(true);
        }
    }

    private static ServiceProvider CreateProvider(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        configuration.AddBeanBotConfiguration(Array.Empty<string>());
        return CreateProvider(configuration);
    }

    private static ServiceProvider CreateProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddBeanBot(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> RequiredSettings() => new()
    {
        [BeanBotConfiguration.BotTokenVariable] = "token",
        [BeanBotConfiguration.MongoConnectionVariable] = "mongodb://localhost:27017",
        [BeanBotConfiguration.GeneralChannelVariable] = "123",
        [BeanBotConfiguration.HatoeteUrlVariable] = "https://example.com/hatoete.png",
        [BeanBotConfiguration.YoshimaruUrlVariable] = "https://example.com/yoshimaru.png"
    };

    private sealed class TemporarySecretFile : IDisposable
    {
        private readonly DirectoryInfo directory;

        internal TemporarySecretFile(string content)
        {
            directory = Directory.CreateTempSubdirectory("beanbot-secret-");
            FilePath = Path.Combine(directory.FullName, "secret");
            File.WriteAllText(FilePath, content, new UTF8Encoding(false));
        }

        internal string DirectoryPath => directory.FullName;
        internal string FilePath { get; }

        public void Dispose()
        {
            directory.Delete(true);
        }
    }
}
