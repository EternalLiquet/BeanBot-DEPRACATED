using BeanBot.Modules;

using Discord;

using Moq;
using Moq.Protected;

using NUnit.Framework;

namespace BeanBotUnitTests
{
    public class HelpModuleUnitTests
    {
        object[] replyAsyncArgList;
        Mock<HelpModule> helpModuleMock;

        [SetUp]
        public void Setup()
        {
            replyAsyncArgList = new object[]
            {
                ItExpr.IsAny<string>(),
                ItExpr.IsNull<bool>(),
                ItExpr.IsNull<Embed>(),
                ItExpr.IsNull<RequestOptions>()
            };
            helpModuleMock = new Mock<HelpModule>();
            helpModuleMock.Protected().Setup(
                "ReplyAsync",
                replyAsyncArgList);
        }

        [Test]
        [Ignore("Needs to be worked on")]
        public void TestHelpCommand()
        {
            helpModuleMock.Object.HelpCommand();
            helpModuleMock.Protected().Verify(
                "ReplyAsync",
                Times.Once(),
                replyAsyncArgList);
        }
    }
}