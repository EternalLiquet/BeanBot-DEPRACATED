using BeanBot.EventHandlers;
using BeanBotUnitTests;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Moq;
using Moq.Protected;

using NUnit.Framework;
using System;

namespace BeanBotUnitTest
{
    public class CommandHandlerUnitTests
    {
        CommandHandler commandHandlerMock;
        Mock<DiscordSocketClient> discordClient;
        Mock<CommandService> commandService;
        [SetUp]
        public void Setup()
        {
            discordClient = new Mock<DiscordSocketClient>();
            commandService = new Mock<CommandService>();
            commandHandlerMock = new CommandHandler(discordClient.Object, commandService.Object);
        }

        [Test]
        public void TestMessageIsSystemMessage()
        {
            var result = commandHandlerMock.MessageIsSystemMessage(null as SocketUserMessage);
            Assert.IsTrue(result);
        }

        [Test]
        public void TestMessageIsAUserMessage()
        {
            object emptyObject = new object() as SocketUserMessage;
            var result = commandHandlerMock.MessageIsSystemMessage(new Mock<SocketUserMessage>().Object);
            Assert.IsFalse(result);
        }
    }
}
