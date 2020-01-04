using BeanBot.Modules;
using Discord;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace BeanBotUnitTests
{
    public class InformationModuleUnitTests
    {
        [SetUp]
        public void Setup()
        {
            
        }

        [Test]
        public void TestDeveloperCommand()
        {
            var argumentList = new object[]
            { 
                ItExpr.IsAny<string>(),
                ItExpr.IsNull<bool>(),
                ItExpr.IsNull<Embed>(),
                ItExpr.IsNull<RequestOptions>()
            };
            var infoModuleMock = new Mock<InfoModule>();
            infoModuleMock.Protected().Setup(
                "ReplyAsync", 
                argumentList);
            infoModuleMock.Object.DeveloperCommand();
            infoModuleMock.Protected().Verify(
                "ReplyAsync", 
                Times.Once(),
                argumentList);
        }
    }
}