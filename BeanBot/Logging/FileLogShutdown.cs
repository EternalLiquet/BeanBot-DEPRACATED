using Serilog;

namespace BeanBot.Logging;

internal static class FileLogShutdown
{
    internal static Task CloseAndFlushAsync()
        => FlushAsync(
            static () => Log.CloseAndFlushAsync().AsTask(),
            FileLogPolicy.ShutdownFlushTimeout,
            static message => Console.Error.WriteLine(message));

    internal static async Task FlushAsync(
        Func<Task> flushAsync,
        TimeSpan timeout,
        Action<string> writeDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(flushAsync);
        ArgumentNullException.ThrowIfNull(writeDiagnostic);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var flushTask = Task.Run(flushAsync);
        try
        {
            await flushTask.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            WriteDiagnosticSafely(
                writeDiagnostic,
                FormattableString.Invariant(
                    $"BeanBot file-log flush exceeded the {timeout.TotalSeconds:0.###}-second shutdown budget; shutdown will continue."));
            ObserveLateFault(flushTask);
        }
        catch (IOException exception)
        {
            WriteDiagnosticSafely(
                writeDiagnostic,
                $"BeanBot file-log flush failed during shutdown: {exception.GetType().Name}.");
        }
        catch (ObjectDisposedException exception)
        {
            WriteDiagnosticSafely(
                writeDiagnostic,
                $"BeanBot file-log flush failed during shutdown: {exception.GetType().Name}.");
        }
    }

    private static void ObserveLateFault(Task task)
        => _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void WriteDiagnosticSafely(Action<string> writeDiagnostic, string message)
    {
        try
        {
            writeDiagnostic(message);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
