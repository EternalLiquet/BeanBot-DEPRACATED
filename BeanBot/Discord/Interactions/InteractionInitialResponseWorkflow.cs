using System.Runtime.ExceptionServices;

namespace BeanBot.Discord.Interactions;

internal sealed record InteractionInitialResponseOperations(
    Func<CancellationToken, Task> SendInitial,
    Func<CancellationToken, Task> ModifyOriginal,
    Func<Exception, bool> IsKnownMissingOriginal);

internal enum InteractionInitialResponseOutcome
{
    Confirmed,
    Absent,
    Unknown
}

internal sealed record InteractionInitialResponseResult(
    InteractionInitialResponseOutcome Outcome,
    Exception? InitialResponseException = null,
    Exception? ReconciliationException = null)
{
    internal bool IsConfirmed => Outcome == InteractionInitialResponseOutcome.Confirmed;

    internal void ThrowIfUnconfirmed()
    {
        if (IsConfirmed)
        {
            return;
        }

        if (InitialResponseException is not null && ReconciliationException is not null)
        {
            throw new AggregateException(
                "The initial interaction response and its reconciliation both failed.",
                InitialResponseException,
                ReconciliationException);
        }

        var exception = InitialResponseException
            ?? ReconciliationException
            ?? new InvalidOperationException(
                "The interaction's initial response could not be confirmed.");
        ExceptionDispatchInfo.Capture(exception).Throw();
    }
}

internal static class InteractionInitialResponseWorkflow
{
    internal static async Task<InteractionInitialResponseResult> ExecuteAsync(
        InteractionExecutionContext executionContext,
        bool supportsOriginalResponse,
        TimeSpan reconciliationTimeout,
        InteractionInitialResponseOperations operations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(operations.SendInitial);
        ArgumentNullException.ThrowIfNull(operations.ModifyOriginal);
        ArgumentNullException.ThrowIfNull(operations.IsKnownMissingOriginal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            reconciliationTimeout,
            TimeSpan.Zero);

        executionContext.BeginInitialResponse(supportsOriginalResponse);
        try
        {
            await operations.SendInitial(cancellationToken);
            executionContext.ConfirmInitialResponse();
            return new InteractionInitialResponseResult(
                InteractionInitialResponseOutcome.Confirmed);
        }
        catch (Exception initialResponseException)
        {
            if (!supportsOriginalResponse)
            {
                return new InteractionInitialResponseResult(
                    InteractionInitialResponseOutcome.Unknown,
                    initialResponseException);
            }

            using var reconciliationCancellation = new CancellationTokenSource(
                reconciliationTimeout);
            try
            {
                await operations.ModifyOriginal(reconciliationCancellation.Token);
                executionContext.ConfirmInitialResponse();
                return new InteractionInitialResponseResult(
                    InteractionInitialResponseOutcome.Confirmed,
                    initialResponseException);
            }
            catch (Exception reconciliationException)
            {
                if (operations.IsKnownMissingOriginal(reconciliationException))
                {
                    executionContext.MarkInitialResponseAbsent();
                    return new InteractionInitialResponseResult(
                        InteractionInitialResponseOutcome.Absent,
                        initialResponseException,
                        reconciliationException);
                }

                return new InteractionInitialResponseResult(
                    InteractionInitialResponseOutcome.Unknown,
                    initialResponseException,
                    reconciliationException);
            }
        }
    }
}
