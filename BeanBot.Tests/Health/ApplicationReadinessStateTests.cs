using BeanBot.Health;
using Xunit;

namespace BeanBot.Tests.Health;

public class ApplicationReadinessStateTests
{
    [Fact]
    public void NewState_StartsUnreadyAndCanBecomeReady()
    {
        var state = new ApplicationReadinessState();

        var starting = state.CreateSnapshot();
        state.MarkReady();
        var ready = state.CreateSnapshot();

        Assert.False(starting.IsReady);
        Assert.Equal(ApplicationLifecycleState.Starting, starting.State);
        Assert.Equal("starting", starting.StateName);
        Assert.True(ready.IsReady);
        Assert.Equal(ApplicationLifecycleState.Ready, ready.State);
        Assert.Equal("ready", ready.StateName);
    }

    [Fact]
    public void BeginDraining_IsTerminalEvenWhenRacingMarkReady()
    {
        for (var iteration = 0; iteration < 250; iteration++)
        {
            var state = new ApplicationReadinessState();

            Parallel.Invoke(state.MarkReady, state.BeginDraining);

            var snapshot = state.CreateSnapshot();
            Assert.False(snapshot.IsReady);
            Assert.Equal(ApplicationLifecycleState.Draining, snapshot.State);
            Assert.Equal("draining", snapshot.StateName);
        }
    }

    [Fact]
    public void RepeatedTransitions_AreIdempotent()
    {
        var state = new ApplicationReadinessState();

        state.MarkReady();
        state.MarkReady();
        state.BeginDraining();
        state.BeginDraining();
        state.MarkReady();

        var snapshot = state.CreateSnapshot();
        Assert.Equal(ApplicationLifecycleState.Draining, snapshot.State);
        Assert.False(snapshot.IsReady);
    }
}
