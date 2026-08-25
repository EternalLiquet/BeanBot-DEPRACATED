namespace BeanBot.Discord.Commands;

internal static class ExternalMediaOperationGuard
{
    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string timeoutMessage)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeoutMessage);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(timeout);

        var operationTask = operation(operationCancellation.Token);
        try
        {
            return await operationTask.WaitAsync(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateFault(operationTask);
            throw;
        }
        catch (OperationCanceledException exception) when (operationCancellation.IsCancellationRequested)
        {
            ObserveLateFault(operationTask);
            throw new TimeoutException(timeoutMessage, exception);
        }
    }

    internal static async Task RunAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string timeoutMessage)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await RunAsync(
            async token =>
            {
                await operation(token);
                return true;
            },
            timeout,
            cancellationToken,
            timeoutMessage);
    }

    internal static void ObserveLateFault(Task operationTask)
    {
        ArgumentNullException.ThrowIfNull(operationTask);

        if (operationTask.IsCompleted)
        {
            _ = operationTask.Exception;
            return;
        }

        _ = operationTask.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
