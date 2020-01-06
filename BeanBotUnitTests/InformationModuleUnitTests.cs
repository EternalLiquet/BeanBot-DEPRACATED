using BeanBot.Modules;

using Discord;

using Moq;
using Moq.Protected;

using NUnit.Framework;

namespace BeanBotUnitTests
{
    public class InformationModuleUnitTests
    {
        object[] replyAsyncArgList;
        Mock<InfoModule> infoModuleMock;

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
            infoModuleMock = new Mock<InfoModule>();
            infoModuleMock.Protected().Setup(
                "ReplyAsync",
                replyAsyncArgList);
        }

        [Test]
        public void TestDeveloperCommand()
        {
            infoModuleMock.Object.DeveloperCommand();
            infoModuleMock.Protected().Verify(
                "ReplyAsync", 
                Times.Once(),
                replyAsyncArgList);
        }
    }
}