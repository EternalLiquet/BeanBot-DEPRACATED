using BeanBot.Discord.Interactions;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class InteractionExecutionContextTests
{
    [Fact]
    public void Enter_ProvidesTokenAndRestoresNestedState()
    {
        var context = new InteractionExecutionContext();
        using var outerCancellation = new CancellationTokenSource();
        using var innerCancellation = new CancellationTokenSource();

        Assert.Equal(CancellationToken.None, context.CancellationToken);
        using (context.Enter(outerCancellation.Token))
        {
            Assert.Equal(outerCancellation.Token, context.CancellationToken);
            using (context.Enter(innerCancellation.Token))
            {
                Assert.Equal(innerCancellation.Token, context.CancellationToken);
            }

            Assert.Equal(outerCancellation.Token, context.CancellationToken);
        }

        Assert.Equal(CancellationToken.None, context.CancellationToken);
    }

    [Fact]
    public void InitialResponse_TracksAttemptConfirmationAndOriginalSupport()
    {
        var context = new InteractionExecutionContext();

        Assert.Equal(
            new InteractionInitialResponseSnapshot(
                InteractionInitialResponseState.None,
                SupportsOriginalResponse: false),
            context.InitialResponse);

        using (context.Enter(CancellationToken.None))
        {
            context.BeginInitialResponse(supportsOriginalResponse: true);
            Assert.Equal(
                new InteractionInitialResponseSnapshot(
                    InteractionInitialResponseState.Attempted,
                    SupportsOriginalResponse: true),
                context.InitialResponse);

            context.ConfirmInitialResponse();
            Assert.Equal(
                new InteractionInitialResponseSnapshot(
                    InteractionInitialResponseState.Confirmed,
                    SupportsOriginalResponse: true),
                context.InitialResponse);
        }

        Assert.Equal(
            InteractionInitialResponseState.None,
            context.InitialResponse.State);
    }

    [Fact]
    public void InitialResponse_CanRecordConfirmedAbsence()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);

        context.BeginInitialResponse(supportsOriginalResponse: true);
        context.MarkInitialResponseAbsent();

        Assert.Equal(
            new InteractionInitialResponseSnapshot(
                InteractionInitialResponseState.Absent,
                SupportsOriginalResponse: true),
            context.InitialResponse);
    }

    [Fact]
    public void Enter_IsolatesAndRestoresNestedInitialResponseState()
    {
        var context = new InteractionExecutionContext();
        using var outerScope = context.Enter(CancellationToken.None);
        context.BeginInitialResponse(supportsOriginalResponse: true);

        using (context.Enter(CancellationToken.None))
        {
            Assert.Equal(
                InteractionInitialResponseState.None,
                context.InitialResponse.State);
            context.BeginInitialResponse(supportsOriginalResponse: false);
            context.ConfirmInitialResponse();
            Assert.False(context.InitialResponse.SupportsOriginalResponse);
        }

        Assert.Equal(
            InteractionInitialResponseState.Attempted,
            context.InitialResponse.State);
        Assert.True(context.InitialResponse.SupportsOriginalResponse);
    }

    [Fact]
    public void BeginInitialResponse_RejectsASecondInitialResponse()
    {
        var context = new InteractionExecutionContext();
        using var scope = context.Enter(CancellationToken.None);
        context.BeginInitialResponse(supportsOriginalResponse: true);

        Assert.Throws<InvalidOperationException>(
            () => context.BeginInitialResponse(supportsOriginalResponse: true));
    }

    [Fact]
    public void InitialResponseTransitions_RequireAnExecutionScopeAndAttempt()
    {
        var context = new InteractionExecutionContext();

        Assert.Throws<InvalidOperationException>(
            () => context.BeginInitialResponse(supportsOriginalResponse: true));

        using var scope = context.Enter(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(context.ConfirmInitialResponse);
        Assert.Throws<InvalidOperationException>(context.MarkInitialResponseAbsent);
    }
}
