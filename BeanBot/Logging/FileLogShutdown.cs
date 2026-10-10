using Serilog;

namespace BeanBot.Logging;

internal static class FileLogShutdown
{
    internal static Task CloseAndFlushAsync()
        => FlushAsync(
            static () => Log.CloseAndFlushAsync().AsTask(),
            FileLogPolicy.ShutdownFlushTimeout,
            FileLogDiagnosticWriter.ConsoleError);

    internal static Task FlushAsync(
        Func<Task> flushAsync,
        TimeSpan timeout,
        Action<string> writeDiagnostic)
        => FlushAsync(flushAsync, timeout, new FileLogDiagnosticWriter(writeDiagnostic));

    private static async Task FlushAsync(
        Func<Task> flushAsync,
        TimeSpan timeout,
        FileLogDiagnosticWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(flushAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var flushTask = Task.Run(flushAsync);
        try
        {
            await flushTask.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLateFault(flushTask);
            diagnostics.ReportShutdown(
                FormattableString.Invariant(
                    $"BeanBot file-log flush exceeded the {timeout.TotalSeconds:0.###}-second shutdown budget; shutdown will continue."));
        }
        catch (IOException exception)
        {
            diagnostics.ReportShutdown(
                $"BeanBot file-log flush failed during shutdown: {exception.GetType().Name}.");
        }
        catch (ObjectDisposedException exception)
        {
            diagnostics.ReportShutdown(
                $"BeanBot file-log flush failed during shutdown: {exception.GetType().Name}.");
        }
    }

    private static void ObserveLateFault(Task task)
        => _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
