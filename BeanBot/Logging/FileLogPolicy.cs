using System.Globalization;
using Serilog;
using Serilog.Configuration;
using Serilog.Sinks.Async;

namespace BeanBot.Logging;

internal readonly record struct FileLogSinkOptions(
    long FileSizeLimitBytes,
    int RetainedFileCountLimit,
    int AsyncBufferSize,
    bool BlockWhenFull);

internal static class FileLogPolicy
{
    internal const long FileSizeLimitBytes = 25L * 1024 * 1024;
    internal const int RetainedFileCountLimit = 8;
    internal const int AsyncBufferSize = 2_048;
    internal const bool BlockWhenFull = false;
    internal const long ConfiguredRetentionBytes = FileSizeLimitBytes * RetainedFileCountLimit;

    internal static readonly TimeSpan DropReportInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    internal static FileLogSinkOptions ProductionOptions => new(
        FileSizeLimitBytes,
        RetainedFileCountLimit,
        AsyncBufferSize,
        BlockWhenFull);

    internal static void ConfigureFileSink(
        LoggerSinkConfiguration sinkConfiguration,
        string path,
        FileLogSinkOptions options,
        IAsyncLogEventSinkMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(monitor);

        ConfigureAsyncSink(
            sinkConfiguration,
            sink => sink.File(
                path,
                formatProvider: CultureInfo.InvariantCulture,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedFileCountLimit),
            options,
            monitor);
    }

    internal static void ConfigureAsyncSink(
        LoggerSinkConfiguration sinkConfiguration,
        Action<LoggerSinkConfiguration> configureSink,
        FileLogSinkOptions options,
        IAsyncLogEventSinkMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);
        ArgumentNullException.ThrowIfNull(configureSink);
        ArgumentNullException.ThrowIfNull(monitor);

        if (options.FileSizeLimitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The file size limit must be positive.");
        }

        if (options.RetainedFileCountLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "At least one file must be retained.");
        }

        if (options.AsyncBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The async buffer must have positive capacity.");
        }

        sinkConfiguration.Async(
            configureSink,
            bufferSize: options.AsyncBufferSize,
            blockWhenFull: options.BlockWhenFull,
            monitor: monitor);
    }
}
