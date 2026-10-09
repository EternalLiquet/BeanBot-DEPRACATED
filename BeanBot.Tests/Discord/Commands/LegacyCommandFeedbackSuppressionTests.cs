using BeanBot.Discord.Commands;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class LegacyCommandFeedbackSuppressionTests
{
    [Fact]
    public void ShouldSuppressFeedback_AmbiguousReplyTimeout_ReturnsTrue()
    {
        var exception = new LegacyCommandReplyTimeoutException(
            "timed out",
            new OperationCanceledException());

        Assert.True(LegacyCommandFeedbackResponder.ShouldSuppressFeedback(exception));
    }

    [Fact]
    public void ShouldSuppressFeedback_CapacityOrShutdownRejection_ReturnsTrue()
    {
        var exception = new LegacyCommandReplyRejectedException("rejected");

        Assert.True(LegacyCommandFeedbackResponder.ShouldSuppressFeedback(exception));
    }

    [Fact]
    public void ShouldSuppressFeedback_ShutdownCancellation_ReturnsTrue()
    {
        Assert.True(LegacyCommandFeedbackResponder.ShouldSuppressFeedback(
            new OperationCanceledException()));
    }

    [Fact]
    public void ShouldSuppressFeedback_UnrelatedCommandFailure_ReturnsFalse()
    {
        Assert.False(LegacyCommandFeedbackResponder.ShouldSuppressFeedback(
            new InvalidOperationException("normal command failure")));
    }
}
