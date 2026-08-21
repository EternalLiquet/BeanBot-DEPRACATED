using BeanBot.Discord.Events;
using BeanBot.Discord.Messaging;
using Xunit;

namespace BeanBot.Tests.Discord.Events;

public class CommandHandlerRoutingTests
{
    [Theory]
    [InlineData(CommandHandler.CommandPrefixKind.Succ)]
    [InlineData(CommandHandler.CommandPrefixKind.Mention)]
    [InlineData(CommandHandler.CommandPrefixKind.Percent)]
    public void CommandPrefix_RoutesOnlyToCommandExecution(CommandHandler.CommandPrefixKind commandPrefix)
    {
        var route = CommandHandler.ResolveMessageRoute(
            isSystemMessage: false,
            isBot: false,
            commandPrefix);

        Assert.Equal(CommandHandler.CommandMessageRoute.ExecuteCommand, route);
    }

    [Fact]
    public void NonCommandUserMessage_RoutesToMessageWaiter()
    {
        var route = CommandHandler.ResolveMessageRoute(
            isSystemMessage: false,
            isBot: false,
            CommandHandler.CommandPrefixKind.None);

        Assert.Equal(CommandHandler.CommandMessageRoute.PublishToMessageWaiter, route);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SystemOrBotMessage_IsIgnored(bool isSystemMessage, bool isBot)
    {
        var route = CommandHandler.ResolveMessageRoute(
            isSystemMessage,
            isBot,
            CommandHandler.CommandPrefixKind.None);

        Assert.Equal(CommandHandler.CommandMessageRoute.Ignore, route);
    }

    [Fact]
    public async Task CommandRoute_DoesNotConsumePendingInteractionAnswer()
    {
        using var waiter = new BoundedMessageWaiter<string>(1);
        var pendingAnswer = waiter.WaitAsync(10, 20, TimeSpan.FromSeconds(1));

        var commandRoute = CommandHandler.ResolveMessageRoute(
            isSystemMessage: false,
            isBot: false,
            CommandHandler.CommandPrefixKind.Percent);
        if (commandRoute == CommandHandler.CommandMessageRoute.PublishToMessageWaiter)
        {
            waiter.TryPublish(10, 20, isBot: false, "%help");
        }

        Assert.False(pendingAnswer.IsCompleted);

        var answerRoute = CommandHandler.ResolveMessageRoute(
            isSystemMessage: false,
            isBot: false,
            CommandHandler.CommandPrefixKind.None);
        Assert.Equal(CommandHandler.CommandMessageRoute.PublishToMessageWaiter, answerRoute);
        Assert.True(waiter.TryPublish(10, 20, isBot: false, "2"));

        Assert.Equal("2", await pendingAnswer);
    }
}
