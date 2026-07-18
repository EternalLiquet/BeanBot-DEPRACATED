using BeanBot.Util;
using Xunit;

namespace BeanBot.Tests.Util;

public class QuestionValidatorTests
{
    [Theory]
    [InlineData("Will I win?")]
    [InlineData("is this rigged?")]
    [InlineData("Should <@123> post?")]
    [InlineData("Why?")]
    [InlineData("What now?")]
    [InlineData("Do you think I'll win?!")]
    [InlineData("**Can I win?**")]
    [InlineData("BeanBot, will I win?")]
    [InlineData("In 2027, will I win?")]
    [InlineData("This works, doesn't it?")]
    [InlineData("This works, right?")]
    [InlineData("Will this still work?   ")]
    [InlineData("Can I?")]
    [InlineData("Should we?")]
    [InlineData("Will they?")]
    [InlineData("What's happening?")]
    [InlineData("Who\u2019s there?")]
    [InlineData("||Can I win?||")]
    [InlineData("In this link https://example.com, will I win?")]
    [InlineData("Have you had lunch?")]
    [InlineData("Had he had enough?")]
    public void AcceptsQuestions(string text)
    {
        Assert.True(QuestionValidator.IsQuestion(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?")]
    [InlineData("!?")]
    [InlineData("??")]
    [InlineData("...???")]
    [InlineData("  ?  ")]
    [InlineData(" !? ")]
    [InlineData("\U0001F914?")]
    [InlineData("<@123>?")]
    [InlineData("<:hmm:123>?")]
    [InlineData("https://example.com/path?")]
    [InlineData("hello?")]
    [InlineData("I am winning?")]
    [InlineData("Bean Bot is cool?")]
    [InlineData("I like beans?")]
    [InlineData("What a day?")]
    [InlineData("Will?")]
    [InlineData("This is not a question, obviously?")]
    [InlineData("This is not a question, will?")]
    [InlineData("Will this work")]
    [InlineData("Will Smith?")]
    [InlineData("Will Smith is an actor?")]
    [InlineData("May flowers?")]
    [InlineData("May is a month?")]
    [InlineData("Can opener?")]
    [InlineData("Need for Speed is a game?")]
    [InlineData("What I wrote is a sentence?")]
    [InlineData("Who I am is obvious?")]
    [InlineData("How I met your mother is a TV show?")]
    [InlineData("what'garbage I wrote is a sentence?")]
    public void RejectsNonQuestions(string? text)
    {
        Assert.False(QuestionValidator.IsQuestion(text));
    }
}
