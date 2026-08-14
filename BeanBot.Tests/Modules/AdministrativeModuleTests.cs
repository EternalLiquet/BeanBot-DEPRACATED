using BeanBot.Modules;
using Xunit;

namespace BeanBot.Tests.Modules;

public class AdministrativeModuleTests
{
    private static readonly AdministrativeModule.RoleCandidate LeagueRole = new(1, "League");
    private static readonly AdministrativeModule.RoleCandidate OtherRole = new(2, "Other");

    [Fact]
    public void ResolveRole_WithExplicitMention_ResolvesMentionedRole()
    {
        var result = Resolve("<@&1>", [LeagueRole], [LeagueRole.Id]);

        AssertResolved(result, LeagueRole.Id);
    }

    [Fact]
    public void ResolveRole_WithPlainTextName_ResolvesMatchingRole()
    {
        var result = Resolve("League", [LeagueRole]);

        AssertResolved(result, LeagueRole.Id);
    }

    [Fact]
    public void ResolveRole_WithDifferentNameCasing_ResolvesMatchingRole()
    {
        var result = Resolve("league", [LeagueRole]);

        AssertResolved(result, LeagueRole.Id);
    }

    [Fact]
    public void ResolveRole_WithMentionAndOtherRoleName_PrefersMentionedRole()
    {
        var result = Resolve("Please use Other instead", [LeagueRole, OtherRole], [LeagueRole.Id]);

        AssertResolved(result, LeagueRole.Id);
    }

    [Fact]
    public void ResolveRole_WithUnknownName_ReturnsNotFound()
    {
        var result = Resolve("Unknown", [LeagueRole]);

        Assert.Equal(AdministrativeModule.RoleResolutionStatus.NotFound, result.Status);
        Assert.Null(result.RoleId);
    }

    [Fact]
    public void ResolveRole_WithMultipleDistinctMentions_ReturnsMultipleMentions()
    {
        var result = Resolve("League", [LeagueRole, OtherRole], [LeagueRole.Id, OtherRole.Id]);

        Assert.Equal(AdministrativeModule.RoleResolutionStatus.MultipleMentions, result.Status);
        Assert.Null(result.RoleId);
    }

    [Fact]
    public void ResolveRole_WithRepeatedMentionOfSameRole_ResolvesThatRole()
    {
        var result = Resolve("unrelated text", [LeagueRole], [LeagueRole.Id, LeagueRole.Id]);

        AssertResolved(result, LeagueRole.Id);
    }

    [Fact]
    public void ResolveRole_WithMentionMissingFromGuild_DoesNotFallBackToName()
    {
        var result = Resolve("League", [LeagueRole], [99]);

        Assert.Equal(AdministrativeModule.RoleResolutionStatus.NotFound, result.Status);
        Assert.Null(result.RoleId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRole_WithBlankName_ReturnsNotFound(string roleName)
    {
        var result = Resolve(roleName, [LeagueRole]);

        Assert.Equal(AdministrativeModule.RoleResolutionStatus.NotFound, result.Status);
        Assert.Null(result.RoleId);
    }

    [Fact]
    public void ResolveRole_WithDuplicateCaseInsensitiveNames_ReturnsAmbiguousName()
    {
        var duplicateLeagueRole = new AdministrativeModule.RoleCandidate(3, "LEAGUE");

        var result = Resolve("league", [LeagueRole, duplicateLeagueRole]);

        Assert.Equal(AdministrativeModule.RoleResolutionStatus.AmbiguousName, result.Status);
        Assert.Null(result.RoleId);
    }

    private static AdministrativeModule.RoleResolution Resolve(
        string roleName,
        IEnumerable<AdministrativeModule.RoleCandidate> availableRoles,
        IEnumerable<ulong>? mentionedRoleIds = null)
    {
        return AdministrativeModule.ResolveRole(roleName, mentionedRoleIds ?? [], availableRoles);
    }

    private static void AssertResolved(AdministrativeModule.RoleResolution result, ulong expectedRoleId)
    {
        Assert.Equal(AdministrativeModule.RoleResolutionStatus.Resolved, result.Status);
        Assert.Equal(expectedRoleId, result.RoleId);
    }
}
