using BeanBot.EventHandlers;

using Discord.Commands;
using Discord.WebSocket;

using Moq;

using NUnit.Framework;
using System.Collections.Generic;

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
    }
}
