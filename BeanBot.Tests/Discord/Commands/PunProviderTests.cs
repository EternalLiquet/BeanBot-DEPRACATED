using BeanBot.Discord.Commands;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public sealed class PunProviderTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"beanbot-pun-provider-{Guid.NewGuid():N}");

    public PunProviderTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void TryGetRandomPun_LoadsUsableRowsAndIgnoresBlankRows()
    {
        var path = WriteResource("BadPost\n\"First pun\"\n\"\"\n\"Second pun\"\n");
        var logger = new RecordingLogger<PunProvider>();
        var provider = new PunProvider(path, logger);

        var selected = GetPun(provider);

        Assert.Contains(selected, new[] { "First pun", "Second pun" });
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("Loaded 2 puns", StringComparison.Ordinal));
    }

    [Fact]
    public void TryGetRandomPun_DoesNotReadResourceAgainAfterLoading()
    {
        var path = WriteResource("BadPost\n\"Original pun\"\n");
        var provider = new PunProvider(path, new RecordingLogger<PunProvider>());
        File.WriteAllText(path, "BadPost\n\"Replacement pun\"\n");

        Assert.Equal("Original pun", GetPun(provider));

        File.Delete(path);
        Assert.Equal("Original pun", GetPun(provider));
    }

    [Theory]
    [InlineData("")]
    [InlineData("BadPost\n")]
    [InlineData("BadPost\n\"\"\n   \n")]
    public void TryGetRandomPun_EmptyResourceIsUnavailable(string contents)
    {
        var logger = new RecordingLogger<PunProvider>();
        var provider = new PunProvider(WriteResource(contents), logger);

        Assert.False(provider.TryGetRandomPun(out var pun));
        Assert.Null(pun);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("No usable puns", StringComparison.Ordinal));
    }

    [Fact]
    public void TryGetRandomPun_MissingResourceIsUnavailable()
    {
        var logger = new RecordingLogger<PunProvider>();
        var provider = new PunProvider(Path.Combine(_temporaryDirectory, "missing.csv"), logger);

        Assert.False(provider.TryGetRandomPun(out var pun));
        Assert.Null(pun);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("was not found", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("WrongHeader\n\"Not a pun\"\n")]
    [InlineData("BadPost\n\"Valid first row\"\nbad\"quote\n")]
    public void TryGetRandomPun_InvalidResourceDoesNotExposePartialResults(string contents)
    {
        var logger = new RecordingLogger<PunProvider>();
        var provider = new PunProvider(WriteResource(contents), logger);

        Assert.False(provider.TryGetRandomPun(out var pun));
        Assert.Null(pun);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("could not be loaded", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private string WriteResource(string contents)
    {
        var path = Path.Combine(_temporaryDirectory, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, contents);
        return path;
    }

    private static string GetPun(PunProvider provider)
    {
        Assert.True(provider.TryGetRandomPun(out var pun));
        return pun;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
