using System.Diagnostics.CodeAnalysis;
using BeanBot.Discord.Commands;
using BeanBot.Discord.Interactions;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class BeanBotInteractionModuleTests
{
    [Fact]
    public void GetPunResponse_WhenPunExists_ReturnsProviderValue()
    {
        var response = BeanBotInteractionModule.GetPunResponse(new StubPunProvider("bean there, done that"));

        Assert.Equal("bean there, done that", response);
    }

    [Fact]
    public void GetPunResponse_WhenProviderIsEmpty_ReturnsLegacyFallback()
    {
        var response = BeanBotInteractionModule.GetPunResponse(new StubPunProvider(null));

        Assert.Equal("The PunMaster is temporarily out of material.", response);
    }

    private sealed class StubPunProvider(string? pun) : IPunProvider
    {
        public bool TryGetRandomPun([NotNullWhen(true)] out string? result)
        {
            result = pun;
            return result is not null;
        }
    }
}
