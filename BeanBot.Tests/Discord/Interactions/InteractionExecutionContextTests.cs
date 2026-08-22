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
}
