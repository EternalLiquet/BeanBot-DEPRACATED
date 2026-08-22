using BeanBot.Discord.Interactions;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class InteractionFailureResponseWorkflowTests
{
    [Fact]
    public async Task ExecuteAsync_ConfirmedResponse_ModifiesOnly()
    {
        var fake = new RecordingOperations();

        var result = await ExecuteAsync(
            InteractionInitialResponseState.Confirmed,
            supportsOriginalResponse: true,
            discordHasResponded: false,
            fake);

        Assert.Equal(
            InteractionFailureResponseOutcome.ModifiedOriginal,
            result.Outcome);
        Assert.Equal(["modify"], fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_DiscordHasResponded_OverridesUntrackedStateAndModifiesOnly()
    {
        var fake = new RecordingOperations();

        var result = await ExecuteAsync(
            InteractionInitialResponseState.None,
            supportsOriginalResponse: false,
            discordHasResponded: true,
            fake);

        Assert.Equal(
            InteractionFailureResponseOutcome.ModifiedOriginal,
            result.Outcome);
        Assert.Equal(["modify"], fake.Calls);
    }

    [Theory]
    [InlineData((int)InteractionInitialResponseState.None)]
    [InlineData((int)InteractionInitialResponseState.Absent)]
    public async Task ExecuteAsync_KnownUnacknowledgedState_SendsInitialOnly(int stateValue)
    {
        var fake = new RecordingOperations();

        var result = await ExecuteAsync(
            (InteractionInitialResponseState)stateValue,
            supportsOriginalResponse: true,
            discordHasResponded: false,
            fake);

        Assert.Equal(InteractionFailureResponseOutcome.SentInitial, result.Outcome);
        Assert.Equal(["initial"], fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_AttemptedResponseWithOriginal_ModifiesFirst()
    {
        var fake = new RecordingOperations();

        var result = await ExecuteAsync(
            InteractionInitialResponseState.Attempted,
            supportsOriginalResponse: true,
            discordHasResponded: false,
            fake);

        Assert.Equal(
            InteractionFailureResponseOutcome.ModifiedOriginal,
            result.Outcome);
        Assert.Equal(["modify"], fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_AttemptedResponseMissingOriginal_FallsBackToInitialOnce()
    {
        var missingFailure = new InvalidOperationException("unknown interaction");
        var fake = new RecordingOperations
        {
            ModifyBehavior = _ => Task.FromException(missingFailure),
            KnownMissing = missingFailure
        };

        var result = await ExecuteAsync(
            InteractionInitialResponseState.Attempted,
            supportsOriginalResponse: true,
            discordHasResponded: false,
            fake);

        Assert.Equal(InteractionFailureResponseOutcome.SentInitial, result.Outcome);
        Assert.Null(result.Exception);
        Assert.Same(missingFailure, result.PriorException);
        Assert.Equal(["modify", "initial"], fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_AttemptedResponseAmbiguousFailure_SuppressesInitialRetry()
    {
        var ambiguousFailure = new InvalidOperationException("gateway failed");
        var fake = new RecordingOperations
        {
            ModifyBehavior = _ => Task.FromException(ambiguousFailure)
        };

        var result = await ExecuteAsync(
            InteractionInitialResponseState.Attempted,
            supportsOriginalResponse: true,
            discordHasResponded: false,
            fake);

        Assert.Equal(InteractionFailureResponseOutcome.Suppressed, result.Outcome);
        Assert.Same(ambiguousFailure, result.Exception);
        Assert.Equal(["modify"], fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_AttemptedModal_SuppressesAllFailureResponses()
    {
        var fake = new RecordingOperations();

        var result = await ExecuteAsync(
            InteractionInitialResponseState.Attempted,
            supportsOriginalResponse: false,
            discordHasResponded: false,
            fake);

        Assert.Equal(InteractionFailureResponseOutcome.Suppressed, result.Outcome);
        Assert.Null(result.Exception);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_ConfirmedModal_SuppressesAllFailureResponses()
    {
        var fake = new RecordingOperations();

        var result = await ExecuteAsync(
            InteractionInitialResponseState.Confirmed,
            supportsOriginalResponse: false,
            discordHasResponded: true,
            fake);

        Assert.Equal(InteractionFailureResponseOutcome.Suppressed, result.Outcome);
        Assert.Null(result.Exception);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_ConfirmedModifyFailure_DoesNotRetryInitial()
    {
        var modifyFailure = new InvalidOperationException("modify failed");
        var fake = new RecordingOperations
        {
            ModifyBehavior = _ => Task.FromException(modifyFailure),
            KnownMissing = modifyFailure
        };

        var result = await ExecuteAsync(
            InteractionInitialResponseState.Confirmed,
            supportsOriginalResponse: true,
            discordHasResponded: false,
            fake);

        Assert.Equal(InteractionFailureResponseOutcome.Failed, result.Outcome);
        Assert.Same(modifyFailure, result.Exception);
        Assert.Equal(["modify"], fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_InitialFailure_DoesNotTryModify()
    {
        var initialFailure = new InvalidOperationException("initial failed");
        var fake = new RecordingOperations
        {
            InitialBehavior = _ => Task.FromException(initialFailure)
        };

        var result = await ExecuteAsync(
            InteractionInitialResponseState.None,
            supportsOriginalResponse: false,
            discordHasResponded: false,
            fake);

        Assert.Equal(InteractionFailureResponseOutcome.Failed, result.Outcome);
        Assert.Same(initialFailure, result.Exception);
        Assert.Equal(["initial"], fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_MissingThenInitialFailure_PreservesBothFailures()
    {
        var missingFailure = new InvalidOperationException("unknown interaction");
        var initialFailure = new InvalidOperationException("initial failed");
        var fake = new RecordingOperations
        {
            ModifyBehavior = _ => Task.FromException(missingFailure),
            InitialBehavior = _ => Task.FromException(initialFailure),
            KnownMissing = missingFailure
        };

        var result = await ExecuteAsync(
            InteractionInitialResponseState.Attempted,
            supportsOriginalResponse: true,
            discordHasResponded: false,
            fake);

        Assert.Equal(InteractionFailureResponseOutcome.Failed, result.Outcome);
        Assert.Same(initialFailure, result.Exception);
        Assert.Same(missingFailure, result.PriorException);
        Assert.Equal(["modify", "initial"], fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_AttemptedCancellation_IsAmbiguousAndNeverDoubleSends()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fake = new RecordingOperations
        {
            ModifyBehavior = token => Task.FromCanceled(token)
        };

        var result = await ExecuteAsync(
            InteractionInitialResponseState.Attempted,
            supportsOriginalResponse: true,
            discordHasResponded: false,
            fake,
            cancellation.Token);

        Assert.Equal(InteractionFailureResponseOutcome.Suppressed, result.Outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        Assert.Equal(["modify"], fake.Calls);
        Assert.Equal(cancellation.Token, fake.ModifyToken);
    }

    [Fact]
    public async Task ExecuteAsync_UnattemptedCancellation_ReportsFailedInitialOnly()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fake = new RecordingOperations
        {
            InitialBehavior = token => Task.FromCanceled(token)
        };

        var result = await ExecuteAsync(
            InteractionInitialResponseState.None,
            supportsOriginalResponse: false,
            discordHasResponded: false,
            fake,
            cancellation.Token);

        Assert.Equal(InteractionFailureResponseOutcome.Failed, result.Outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        Assert.Equal(["initial"], fake.Calls);
        Assert.Equal(cancellation.Token, fake.InitialToken);
    }

    private static Task<InteractionFailureResponseResult> ExecuteAsync(
        InteractionInitialResponseState state,
        bool supportsOriginalResponse,
        bool discordHasResponded,
        RecordingOperations fake,
        CancellationToken cancellationToken = default)
        => InteractionFailureResponseWorkflow.ExecuteAsync(
            new InteractionInitialResponseSnapshot(state, supportsOriginalResponse),
            discordHasResponded,
            fake.Create(),
            cancellationToken);

    private sealed class RecordingOperations
    {
        internal List<string> Calls { get; } = [];

        internal Func<CancellationToken, Task>? ModifyBehavior { get; init; }

        internal Func<CancellationToken, Task>? InitialBehavior { get; init; }

        internal Exception? KnownMissing { get; init; }

        internal CancellationToken ModifyToken { get; private set; }

        internal CancellationToken InitialToken { get; private set; }

        internal InteractionFailureResponseOperations Create()
            => new(ModifyAsync, SendInitialAsync, IsKnownMissing);

        private Task ModifyAsync(CancellationToken cancellationToken)
        {
            Calls.Add("modify");
            ModifyToken = cancellationToken;
            return ModifyBehavior?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        private Task SendInitialAsync(CancellationToken cancellationToken)
        {
            Calls.Add("initial");
            InitialToken = cancellationToken;
            return InitialBehavior?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        private bool IsKnownMissing(Exception exception)
            => ReferenceEquals(exception, KnownMissing);
    }
}
