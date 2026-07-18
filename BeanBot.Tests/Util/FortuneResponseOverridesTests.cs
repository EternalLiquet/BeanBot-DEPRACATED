using BeanBot.Util;
using Xunit;

namespace BeanBot.Tests.Util;

public class FortuneResponseOverridesTests
{
    [Theory]
    [InlineData("Should I shower?")]
    [InlineData("Should I take a shower?")]
    [InlineData("Do I need to shower?")]
    [InlineData("Shouldn't I shower?")]
    [InlineData("Should I not shower?")]
    [InlineData("Should I not not shower?")]
    [InlineData("Shouldn't I not shower?")]
    [InlineData("Should I not avoid showering?")]
    [InlineData("Wouldn't it be bad if I didn't shower?")]
    [InlineData("Is there any reason I shouldn't shower?")]
    [InlineData("Don't I need to shower?")]
    [InlineData("Is it time for me to take a shower?")]
    [InlineData("Is avoiding a shower a good idea?")]
    [InlineData("Would it be smart to stay unshowered?")]
    [InlineData("SHOULD I SHOWER?!")]
    [InlineData("Should I bathe?")]
    [InlineData("Should I take a bath?")]
    [InlineData("Do I have to shower?")]
    [InlineData("Am I required to shower?")]
    [InlineData("Would I be wrong not to shower?")]
    [InlineData("Would you recommend that I shower?")]
    [InlineData("Should I shower before the baby shower?")]
    [InlineData("Should I take a shower after I clean my shower?")]
    [InlineData("Should I shower my dog and then shower myself?")]
    [InlineData("I shouldn't shower, right?")]
    [InlineData("I should shower, shouldn't I?")]
    [InlineData("I shouldn't not shower, should I?")]
    [InlineData("Should I clean my shower before taking a shower?")]
    [InlineData("Should I shower my dog and then take a shower?")]
    [InlineData("Should I even shower?")]
    [InlineData("Should I seriously shower?")]
    [InlineData("Should I finally shower?")]
    [InlineData("Should I take a warm shower?")]
    [InlineData("Should I take a short shower?")]
    [InlineData("Should I take my shower?")]
    [InlineData("Should I go and shower?")]
    [InlineData("Is it a good idea to shower?")]
    [InlineData("Is it a bad idea not to shower?")]
    [InlineData("Is showering recommended?")]
    public void ShowerAdviceQuestionsGetAnUnambiguousYes(string question)
    {
        Assert.Equal(FortuneResponseOverrides.ShowerResponse, FortuneResponseOverrides.GetResponse(question));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("shower?")]
    [InlineData("Should I shower")]
    [InlineData("Should I buy a shower curtain?")]
    [InlineData("Should I attend a baby shower?")]
    [InlineData("Will there be rain showers?")]
    [InlineData("Should I clean my shower?")]
    [InlineData("Why is the shower broken?")]
    [InlineData("Should I shower my dog?")]
    [InlineData("Should I tell Bob to shower?")]
    [InlineData("Should I replace the showerhead?")]
    [InlineData("Is my shower okay?")]
    [InlineData("Should I buy a bath towel?")]
    [InlineData("Should I clean my bath?")]
    [InlineData("Should I sing in the shower?")]
    [InlineData("Should I eat after showering?")]
    [InlineData("What good music should I play while showering?")]
    [InlineData("Is my singing okay while showering?")]
    [InlineData("Should I photograph myself in the shower?")]
    [InlineData("Should I give Fido a shower?")]
    [InlineData("Should I bathe my dog?")]
    [InlineData("Should I give my dog a bath?")]
    [InlineData("Is taking a shower curtain good?")]
    [InlineData("Should Bob wait while I shower?")]
    [InlineData("Should Bob call me while I shower?")]
    [InlineData("What time did I shower?")]
    [InlineData("Did I shower before the bad movie?")]
    [InlineData("Is the shower good?")]
    [InlineData("Was the shower bad?")]
    public void UnrelatedOrInvalidShowerTextDoesNotGetAnOverride(string? question)
    {
        Assert.Null(FortuneResponseOverrides.GetResponse(question));
    }
}
