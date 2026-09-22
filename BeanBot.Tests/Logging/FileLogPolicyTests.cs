using BeanBot.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace BeanBot.Tests.Logging;

public sealed class FileLogPolicyTests
{
    [Fact]
    public void ProductionPolicy_HasExplicitBoundedStorageAndAsyncQueue()
    {
        var options = FileLogPolicy.ProductionOptions;

        Assert.Equal(25L * 1024 * 1024, options.FileSizeLimitBytes);
        Assert.Equal(8, options.RetainedFileCountLimit);
        Assert.Equal(2_048, options.AsyncBufferSize);
        Assert.False(options.BlockWhenFull);
        Assert.Equal(200L * 1024 * 1024, FileLogPolicy.ConfiguredRetentionBytes);
    }

    [Fact]
    public void ConfigureFileSink_RollsBySizeAndRetainsOnlyNewestFiles()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"beanbot-file-log-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var path = Path.Combine(tempDirectory, "BeanBotLogs.txt");
            var monitor = new FileLogDropMonitor(
                TimeSpan.FromHours(1),
                static _ => { });
            var options = new FileLogSinkOptions(
                FileSizeLimitBytes: 512,
                RetainedFileCountLimit: 3,
                AsyncBufferSize: 256,
                BlockWhenFull: false);
            var configuration = new LoggerConfiguration().MinimumLevel.Verbose();
            FileLogPolicy.ConfigureFileSink(configuration.WriteTo, path, options, monitor);

            using (var logger = configuration.CreateLogger())
            {
                for (var index = 0; index < 40; index++)
                {
                    logger.Information(
                        "File rollover probe {Index}: {Payload}",
                        index,
                        new string('x', 128));
                }
            }

            var files = Directory.GetFiles(tempDirectory, "BeanBotLogs*.txt");
            Assert.InRange(files.Length, 2, options.RetainedFileCountLimit);
            Assert.Contains(
                files,
                file => File.ReadAllText(file).Contains(
                    "File rollover probe",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BoundedAsyncSink_DropsWithoutBlockingAndReportsLossOutOfBand()
    {
        const string sensitivePayload = "never-copy-this-payload";
        var diagnostics = new List<string>();
        var monitor = new FileLogDropMonitor(TimeSpan.FromHours(1), diagnostics.Add);
        var blockingSink = new BlockingSink();
        var options = new FileLogSinkOptions(
            FileSizeLimitBytes: 512,
            RetainedFileCountLimit: 2,
            AsyncBufferSize: 2,
            BlockWhenFull: false);
        var configuration = new LoggerConfiguration().MinimumLevel.Verbose();
        FileLogPolicy.ConfigureAsyncSink(
            configuration.WriteTo,
            sink => sink.Sink(blockingSink),
            options,
            monitor);
        var logger = configuration.CreateLogger();

        try
        {
            logger.Information("Occupy the async worker");
            await blockingSink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var producer = Task.Run(() =>
            {
                for (var index = 0; index < 100; index++)
                {
                    logger.Information("{Payload} {Index}", sensitivePayload, index);
                }
            });

            await producer.WaitAsync(TimeSpan.FromSeconds(2));

            var droppedMessages = monitor.CheckNow();
            monitor.CheckNow();

            Assert.True(droppedMessages > 0);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("dropped", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sensitivePayload, diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            blockingSink.Release();
            logger.Dispose();
        }
    }

    [Fact]
    public async Task FlushAsync_WhenUnderlyingFlushStalls_ReturnsWithinApplicationDeadline()
    {
        var flushCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new List<string>();

        await FileLogShutdown.FlushAsync(
                () => flushCompletion.Task,
                TimeSpan.FromMilliseconds(25),
                diagnostics.Add)
            .WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("shutdown budget", diagnostic, StringComparison.Ordinal);

        flushCompletion.SetException(new IOException("late file-system failure"));
        await Task.Yield();
    }

    [Fact]
    public async Task FlushAsync_WhenFlushDelegateBlocksSynchronously_ReturnsWithinApplicationDeadline()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new List<string>();

        try
        {
            await FileLogShutdown.FlushAsync(
                    () =>
                    {
                        release.Wait();
                        completed.TrySetResult();
                        return Task.CompletedTask;
                    },
                    TimeSpan.FromMilliseconds(25),
                    diagnostics.Add)
                .WaitAsync(TimeSpan.FromSeconds(2));

            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("shutdown budget", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task FlushAsync_WhenStorageFails_ReportsFailureWithoutRecursiveLogging()
    {
        var diagnostics = new List<string>();

        await FileLogShutdown.FlushAsync(
            static () => Task.FromException(new IOException("disk unavailable")),
            TimeSpan.FromSeconds(1),
            diagnostics.Add);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains(nameof(IOException), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("disk unavailable", diagnostic, StringComparison.Ordinal);
    }

    private sealed class BlockingSink : ILogEventSink, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);

        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            Entered.TrySetResult();
            _release.Wait();
        }

        internal void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }
}
