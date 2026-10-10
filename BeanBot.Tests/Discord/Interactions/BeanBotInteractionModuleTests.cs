using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.Puns;
using Discord.Interactions;
using Xunit;

namespace BeanBot.Tests.Discord.Interactions;

public class BeanBotInteractionModuleTests
{
    [Fact]
    public void Constructor_RejectsMissingDependencies()
    {
        var provider = new StubPunProvider(null);
        var executionContext = new InteractionExecutionContext();

        Assert.Throws<ArgumentNullException>(
            () => new BeanBotInteractionModule(null!, executionContext));
        Assert.Throws<ArgumentNullException>(
            () => new BeanBotInteractionModule(provider, null!));
    }

    [Theory]
    [InlineData(nameof(BeanBotInteractionModule.PingAsync))]
    [InlineData(nameof(BeanBotInteractionModule.PunAsync))]
    [InlineData(nameof(BeanBotInteractionModule.HelpAsync))]
    [InlineData("VersionAsync")]
    public void Commands_RunInlineSoFailuresStayInsideTheTrackedExecution(string methodName)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(BeanBotInteractionModule).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public));
        var attribute = Assert.IsType<SlashCommandAttribute>(
            method.GetCustomAttribute<SlashCommandAttribute>());

        Assert.Equal(RunMode.Sync, attribute.RunMode);
    }

    [Fact]
    public void VersionCommand_UsesExpectedName()
    {
        var method = typeof(BeanBotInteractionModule).GetMethod("VersionAsync");
        var attribute = Assert.IsType<SlashCommandAttribute>(
            method?.GetCustomAttribute<SlashCommandAttribute>());

        Assert.Equal("version", attribute.Name);
    }

    [Theory]
    [InlineData("2.18.3", "BeanBot v2.18.3")]
    [InlineData("0.0.0-local", "Development build")]
    [InlineData("unknown", "I can't tell which version I'm running.")]
    [InlineData("", "I can't tell which version I'm running.")]
    [InlineData(null, "I can't tell which version I'm running.")]
    public void FormatVersionResponse_UsesRunningBuildVersion(string? version, string expected)
    {
        Assert.Equal(expected, BeanBotInteractionModule.FormatVersionResponse(version));
    }

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
