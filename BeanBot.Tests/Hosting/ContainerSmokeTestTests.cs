using BeanBot.Hosting;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class ContainerSmokeTestTests
{
    [Fact]
    public void Run_WritableDataDirectoryAndResource_ReturnsSuccessWithoutLeavingProbe()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(temporaryDirectory, "BeanBotFiles");
        var resourcePath = Path.Combine(temporaryDirectory, "puns.csv");
        File.WriteAllText(resourcePath, "BadPost\nA test pun");
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = ContainerSmokeTest.Run(dataDirectory, resourcePath, output, error);

            Assert.Equal(0, result);
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
    public void Run_MissingResource_ReturnsFailureWithoutLeakingPathDetails()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = ContainerSmokeTest.Run(
                Path.Combine(temporaryDirectory, "BeanBotFiles"),
                Path.Combine(temporaryDirectory, "missing.csv"),
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

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"BeanBotSmokeTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
