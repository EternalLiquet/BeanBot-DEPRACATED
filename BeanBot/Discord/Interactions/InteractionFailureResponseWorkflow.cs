namespace BeanBot.Discord.Interactions;

internal sealed record InteractionFailureResponseOperations(
    Func<CancellationToken, Task> ModifyOriginal,
    Func<CancellationToken, Task> SendInitial,
    Func<Exception, bool> IsKnownMissingOriginal);

internal enum InteractionFailureResponseOutcome
{
    ModifiedOriginal,
    SentInitial,
    Suppressed,
    Failed
}

internal sealed record InteractionFailureResponseResult(
    InteractionFailureResponseOutcome Outcome,
    Exception? Exception = null,
    Exception? PriorException = null);

internal static class InteractionFailureResponseWorkflow
{
    internal static async Task<InteractionFailureResponseResult> ExecuteAsync(
        InteractionInitialResponseSnapshot trackedResponse,
        bool discordHasResponded,
        InteractionFailureResponseOperations operations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(operations.ModifyOriginal);
        ArgumentNullException.ThrowIfNull(operations.SendInitial);
        ArgumentNullException.ThrowIfNull(operations.IsKnownMissingOriginal);

        if (trackedResponse.State == InteractionInitialResponseState.Confirmed
            && !trackedResponse.SupportsOriginalResponse)
        {
            return new InteractionFailureResponseResult(
                InteractionFailureResponseOutcome.Suppressed);
        }

        if (discordHasResponded
            || trackedResponse.State == InteractionInitialResponseState.Confirmed)
        {
            return await TryModifyOriginalAsync(operations, cancellationToken);
        }

        if (trackedResponse.State is InteractionInitialResponseState.None
            or InteractionInitialResponseState.Absent)
        {
            return await TrySendInitialAsync(operations, cancellationToken);
        }

        if (trackedResponse.State != InteractionInitialResponseState.Attempted)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trackedResponse),
                trackedResponse,
                "The tracked initial response state is not supported.");
        }

        if (!trackedResponse.SupportsOriginalResponse)
        {
            return new InteractionFailureResponseResult(
                InteractionFailureResponseOutcome.Suppressed);
        }

        try
        {
            await operations.ModifyOriginal(cancellationToken);
            return new InteractionFailureResponseResult(
                InteractionFailureResponseOutcome.ModifiedOriginal);
        }
        catch (Exception modifyException)
        {
            if (!operations.IsKnownMissingOriginal(modifyException))
            {
                return new InteractionFailureResponseResult(
                    InteractionFailureResponseOutcome.Suppressed,
                    modifyException);
            }

            return await TrySendInitialAsync(
                operations,
                cancellationToken,
                modifyException);
        }
    }

    private static async Task<InteractionFailureResponseResult> TryModifyOriginalAsync(
        InteractionFailureResponseOperations operations,
        CancellationToken cancellationToken)
    {
        try
        {
            await operations.ModifyOriginal(cancellationToken);
            return new InteractionFailureResponseResult(
                InteractionFailureResponseOutcome.ModifiedOriginal);
        }
        catch (Exception exception)
        {
            return new InteractionFailureResponseResult(
                InteractionFailureResponseOutcome.Failed,
                exception);
        }
    }

    private static async Task<InteractionFailureResponseResult> TrySendInitialAsync(
        InteractionFailureResponseOperations operations,
        CancellationToken cancellationToken,
        Exception? priorException = null)
    {
        try
        {
            await operations.SendInitial(cancellationToken);
            return new InteractionFailureResponseResult(
                InteractionFailureResponseOutcome.SentInitial,
                PriorException: priorException);
        }
        catch (Exception exception)
        {
            return new InteractionFailureResponseResult(
                InteractionFailureResponseOutcome.Failed,
                exception,
                priorException);
        }
    }
}
