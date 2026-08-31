using BeanBot.Logging;
using Discord;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Messaging;

public sealed class DiscordMessageCleanupService
{
    internal const int MaximumBulkDeleteCount = 100;
    internal static readonly TimeSpan DeleteOperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumBulkDeleteAge = TimeSpan.FromDays(14) - TimeSpan.FromMinutes(5);
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly ILogger<DiscordMessageCleanupService> _logger;

    public DiscordMessageCleanupService(ILogger<DiscordMessageCleanupService> logger)
        : this(() => DateTimeOffset.UtcNow, logger)
    {
    }

    internal DiscordMessageCleanupService(
        Func<DateTimeOffset> getUtcNow,
        ILogger<DiscordMessageCleanupService> logger)
    {
        _getUtcNow = getUtcNow ?? throw new ArgumentNullException(nameof(getUtcNow));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task DeleteAsync(
        ITextChannel channel,
        IReadOnlyCollection<IMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var plan = CreatePlan(messages, message => message.Timestamp, _getUtcNow());
        return ExecutePlanAsync(
            plan,
            (batch, options) => channel.DeleteMessagesAsync(batch, options),
            (message, options) => message.DeleteAsync(options),
            (exception, itemCount, isBatch) => BeanBotLog.MessageCleanupFailed(
                _logger,
                itemCount,
                isBatch ? "bulk deletion" : "individual deletion",
                exception),
            DeleteOperationTimeout,
            cancellationToken);
    }

    internal static MessageCleanupPlan<T> CreatePlan<T>(
        IReadOnlyCollection<T> messages,
        Func<T, DateTimeOffset> getTimestamp,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(getTimestamp);

        var oldestBulkDeleteTimestamp = now.Subtract(MaximumBulkDeleteAge);
        var recent = messages.Where(message => getTimestamp(message) >= oldestBulkDeleteTimestamp).ToList();
        var individual = messages.Where(message => getTimestamp(message) < oldestBulkDeleteTimestamp).ToList();
        var batches = new List<IReadOnlyCollection<T>>();

        for (var offset = 0; offset < recent.Count; offset += MaximumBulkDeleteCount)
        {
            var batch = recent.Skip(offset).Take(MaximumBulkDeleteCount).ToList();
            if (batch.Count == 1)
            {
                individual.Add(batch[0]);
            }
            else if (batch.Count > 1)
            {
                batches.Add(batch);
            }
        }

        return new MessageCleanupPlan<T>(batches, individual);
    }

    internal static async Task ExecutePlanAsync<T>(
        MessageCleanupPlan<T> plan,
        Func<IReadOnlyCollection<T>, RequestOptions, Task> deleteBatch,
        Func<T, RequestOptions, Task> deleteIndividual,
        Action<Exception, int, bool> onFailure,
        TimeSpan? operationTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(deleteBatch);
        ArgumentNullException.ThrowIfNull(deleteIndividual);
        ArgumentNullException.ThrowIfNull(onFailure);

        var timeout = operationTimeout ?? DeleteOperationTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        foreach (var batch in plan.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ExecuteDeleteAsync(
                    options => deleteBatch(batch, options),
                    timeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                onFailure(exception, batch.Count, true);
            }
        }

        foreach (var item in plan.IndividualItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ExecuteDeleteAsync(
                    options => deleteIndividual(item, options),
                    timeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                onFailure(exception, 1, false);
            }
        }
    }

    internal static async Task ExecuteDeleteAsync(
        Func<RequestOptions, Task> delete,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delete);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(timeout);

        var requestOptions = new RequestOptions
        {
            CancelToken = operationCancellation.Token
        };
        var deleteTask = delete(requestOptions);

        try
        {
            await deleteTask.WaitAsync(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveLateFault(deleteTask);
            throw;
        }
        catch (OperationCanceledException exception) when (operationCancellation.IsCancellationRequested)
        {
            _ = ObserveLateFault(deleteTask);
            throw new TimeoutException("Discord message cleanup operation timed out.", exception);
        }
    }

    internal static Task ObserveLateFault(Task operationTask)
    {
        ArgumentNullException.ThrowIfNull(operationTask);

        if (operationTask.IsCompleted)
        {
            _ = operationTask.Exception;
            return Task.CompletedTask;
        }

        return operationTask.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed class MessageCleanupPlan<T>
{
    public MessageCleanupPlan(
        IReadOnlyCollection<IReadOnlyCollection<T>> batches,
        IReadOnlyCollection<T> individualItems)
    {
        Batches = batches;
        IndividualItems = individualItems;
    }

    public IReadOnlyCollection<IReadOnlyCollection<T>> Batches { get; }
    public IReadOnlyCollection<T> IndividualItems { get; }
}
