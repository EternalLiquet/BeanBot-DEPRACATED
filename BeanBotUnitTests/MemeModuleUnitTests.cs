using BeanBot.Modules;

using Discord;
using Discord.WebSocket;
using Moq;
using Moq.Protected;

using NUnit.Framework;

namespace BeanBotUnitTests
{
    public class MemeModuleUnitTests
    {
        object[] replyAsyncArgList;
        Mock<MemeModule> memeModuleMock;
        

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
            memeModuleMock = new Mock<MemeModule>();
            memeModuleMock.Protected().Setup(
                "ReplyAsync",
                replyAsyncArgList);
        }

        [Test]
        public void TestSuccCommand()
        {
            memeModuleMock.Object.UserSucc();
            memeModuleMock.Protected().Verify(
                "ReplyAsync",
                Times.Never(),
                replyAsyncArgList);
        }

        [Test]
        public void TestMcodnaldsCommand()
        {
            memeModuleMock.Object.McDonalds();
            memeModuleMock.Protected().Verify(
                "ReplyAsync",
                Times.Once(),
                replyAsyncArgList);
        }

        [Test]
        public void TestOchoOchoCommand()
        {
            memeModuleMock.Object.OchoOcho();
            memeModuleMock.Protected().Verify(
                "ReplyAsync",
                Times.Exactly(5),
                replyAsyncArgList);
        }
    }
}
