using BeanBot.Discord.Interactions;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class InteractionInitialResponseWorkflowTests
{
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ExecuteAsync_SendSucceeds_ConfirmsWithoutReconciliation()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        var sendCalls = 0;
        var modifyCalls = 0;

        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: true,
            ReconciliationTimeout,
            new InteractionInitialResponseOperations(
                _ =>
                {
                    sendCalls++;
                    return Task.CompletedTask;
                },
                _ =>
                {
                    modifyCalls++;
                    return Task.CompletedTask;
                },
                _ => false),
            CancellationToken.None);

        Assert.Equal(InteractionInitialResponseOutcome.Confirmed, result.Outcome);
        Assert.Null(result.InitialResponseException);
        Assert.Null(result.ReconciliationException);
        Assert.Equal(1, sendCalls);
        Assert.Equal(0, modifyCalls);
        Assert.Equal(
            InteractionInitialResponseState.Confirmed,
            context.InitialResponse.State);
    }

    [Fact]
    public async Task ExecuteAsync_CommitThenThrow_ReconcilesWithFreshTokenAndPreservesFailure()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        using var callerCancellation = new CancellationTokenSource();
        var initialFailure = new InvalidOperationException("send failed after commit");
        var modifyCalls = 0;
        CancellationToken reconciliationToken = default;

        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: true,
            ReconciliationTimeout,
            new InteractionInitialResponseOperations(
                token =>
                {
                    Assert.Equal(callerCancellation.Token, token);
                    return Task.FromException(initialFailure);
                },
                token =>
                {
                    modifyCalls++;
                    reconciliationToken = token;
                    return Task.CompletedTask;
                },
                _ => false),
            callerCancellation.Token);

        Assert.Equal(InteractionInitialResponseOutcome.Confirmed, result.Outcome);
        Assert.Same(initialFailure, result.InitialResponseException);
        Assert.Null(result.ReconciliationException);
        Assert.Equal(1, modifyCalls);
        Assert.True(reconciliationToken.CanBeCanceled);
        Assert.NotEqual(callerCancellation.Token, reconciliationToken);
        Assert.False(reconciliationToken.IsCancellationRequested);
        Assert.Equal(
            InteractionInitialResponseState.Confirmed,
            context.InitialResponse.State);
        result.ThrowIfUnconfirmed();
    }

    [Fact]
    public async Task ExecuteAsync_ReconciliationFindsNoOriginal_RecordsAbsent()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        var initialFailure = new InvalidOperationException("send failed");
        var missingFailure = new InvalidOperationException("unknown interaction");

        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: true,
            ReconciliationTimeout,
            new InteractionInitialResponseOperations(
                _ => Task.FromException(initialFailure),
                _ => Task.FromException(missingFailure),
                exception => ReferenceEquals(exception, missingFailure)),
            CancellationToken.None);

        Assert.Equal(InteractionInitialResponseOutcome.Absent, result.Outcome);
        Assert.Same(initialFailure, result.InitialResponseException);
        Assert.Same(missingFailure, result.ReconciliationException);
        Assert.Equal(
            InteractionInitialResponseState.Absent,
            context.InitialResponse.State);
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousReconciliation_RemainsAttemptedAndUnknown()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        var initialFailure = new InvalidOperationException("send failed");
        var reconciliationFailure = new InvalidOperationException("gateway failed");

        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: true,
            ReconciliationTimeout,
            new InteractionInitialResponseOperations(
                _ => Task.FromException(initialFailure),
                _ => Task.FromException(reconciliationFailure),
                _ => false),
            CancellationToken.None);

        Assert.Equal(InteractionInitialResponseOutcome.Unknown, result.Outcome);
        Assert.Same(initialFailure, result.InitialResponseException);
        Assert.Same(reconciliationFailure, result.ReconciliationException);
        Assert.Equal(
            InteractionInitialResponseState.Attempted,
            context.InitialResponse.State);
    }

    [Fact]
    public async Task ExecuteAsync_ModalFailure_DoesNotProbeForAnOriginalResponse()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        var initialFailure = new InvalidOperationException("modal send failed");
        var modifyCalls = 0;

        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: false,
            ReconciliationTimeout,
            new InteractionInitialResponseOperations(
                _ => Task.FromException(initialFailure),
                _ =>
                {
                    modifyCalls++;
                    return Task.CompletedTask;
                },
                _ => false),
            CancellationToken.None);

        Assert.Equal(InteractionInitialResponseOutcome.Unknown, result.Outcome);
        Assert.Same(initialFailure, result.InitialResponseException);
        Assert.Equal(0, modifyCalls);
        Assert.Equal(
            new InteractionInitialResponseSnapshot(
                InteractionInitialResponseState.Attempted,
                SupportsOriginalResponse: false),
            context.InitialResponse);
    }

    [Fact]
    public async Task ExecuteAsync_SendCancellation_StillReconcilesWithIndependentToken()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        using var callerCancellation = new CancellationTokenSource();
        CancellationToken reconciliationToken = default;

        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: true,
            ReconciliationTimeout,
            new InteractionInitialResponseOperations(
                token =>
                {
                    callerCancellation.Cancel();
                    return Task.FromCanceled(token);
                },
                token =>
                {
                    reconciliationToken = token;
                    return Task.CompletedTask;
                },
                _ => false),
            callerCancellation.Token);

        Assert.Equal(InteractionInitialResponseOutcome.Confirmed, result.Outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(
            result.InitialResponseException);
        Assert.True(callerCancellation.IsCancellationRequested);
        Assert.True(reconciliationToken.CanBeCanceled);
        Assert.False(reconciliationToken.IsCancellationRequested);
        Assert.NotEqual(callerCancellation.Token, reconciliationToken);
    }

    [Fact]
    public async Task ExecuteAsync_ReconciliationCancellation_IsUnknownNotAbsent()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        var initialFailure = new InvalidOperationException("send failed");
        var reconciliationFailure = new OperationCanceledException("probe canceled");

        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: true,
            ReconciliationTimeout,
            new InteractionInitialResponseOperations(
                _ => Task.FromException(initialFailure),
                _ => Task.FromException(reconciliationFailure),
                _ => false),
            CancellationToken.None);

        Assert.Equal(InteractionInitialResponseOutcome.Unknown, result.Outcome);
        Assert.Same(reconciliationFailure, result.ReconciliationException);
        Assert.Equal(
            InteractionInitialResponseState.Attempted,
            context.InitialResponse.State);
    }

    [Fact]
    public async Task ExecuteAsync_ReconciliationThatDoesNotReturn_IsBoundedAndUnknown()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        var initialFailure = new InvalidOperationException("send failed");

        var execution = InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: true,
            TimeSpan.FromMilliseconds(25),
            new InteractionInitialResponseOperations(
                _ => Task.FromException(initialFailure),
                token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                _ => false),
            CancellationToken.None);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(InteractionInitialResponseOutcome.Unknown, result.Outcome);
        Assert.Same(initialFailure, result.InitialResponseException);
        Assert.IsAssignableFrom<OperationCanceledException>(
            result.ReconciliationException);
        Assert.Equal(
            InteractionInitialResponseState.Attempted,
            context.InitialResponse.State);
    }

    [Fact]
    public async Task ThrowIfUnconfirmed_RethrowsOriginalInitialFailure()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        var initialFailure = new InvalidOperationException("send failed");

        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: false,
            ReconciliationTimeout,
            new InteractionInitialResponseOperations(
                _ => Task.FromException(initialFailure),
                _ => Task.CompletedTask,
                _ => false),
            CancellationToken.None);

        var thrown = Assert.Throws<InvalidOperationException>(
            result.ThrowIfUnconfirmed);
        Assert.Same(initialFailure, thrown);
    }

    [Fact]
    public async Task ThrowIfUnconfirmed_PreservesBothIndependentFailures()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        var initialFailure = new InvalidOperationException("send failed");
        var reconciliationFailure = new TimeoutException("probe failed");

        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            context,
            supportsOriginalResponse: true,
            ReconciliationTimeout,
            new InteractionInitialResponseOperations(
                _ => Task.FromException(initialFailure),
                _ => Task.FromException(reconciliationFailure),
                _ => false),
            CancellationToken.None);

        var thrown = Assert.Throws<AggregateException>(result.ThrowIfUnconfirmed);
        Assert.Equal(
            [initialFailure, reconciliationFailure],
            thrown.InnerExceptions);
    }
}
