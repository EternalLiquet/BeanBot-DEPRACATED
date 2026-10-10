using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class LegacyReactionRoleRetirementComponentsTests
{
    [Fact]
    public void BuildConfirmationEmbed_ShowsConfiguredRoleEmoteMappings()
    {
        var preview = new LegacyReactionRoleRetirementPreview(
            new LegacyReactionRoleSource(1, 2, 3, [4, 6]),
            "Games",
            SourceWasMissing: false,
            [
                new LegacyReactionRoleRetirementMapping(4, "5", "smile", "Gamer"),
                new LegacyReactionRoleRetirementMapping(6, "7", "wave", "Reader")
            ]);

        var embed = LegacyReactionRoleRetirementComponents.BuildConfirmationEmbed(preview);
        var mappingText = string.Join("\n", embed.Fields.Select(field => field.Value));

        Assert.Contains(":smile: → Gamer", mappingText, StringComparison.Ordinal);
        Assert.Contains(":wave: → Reader", mappingText, StringComparison.Ordinal);
        Assert.Contains(
            "Existing member roles are not changed.",
            embed.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfirmationEmbed_BoundsMappingsAcrossFields()
    {
        var mappings = Enumerable.Range(1, 25)
            .Select(index => new LegacyReactionRoleRetirementMapping(
                (ulong)(100 + index),
                (200 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"emoji{index}", $"Role {index}"))
            .ToArray();
        var preview = new LegacyReactionRoleRetirementPreview(
            new LegacyReactionRoleSource(
                1,
                2,
                3,
                mappings.Select(mapping => mapping.RoleId).ToArray()),
            "Games",
            SourceWasMissing: false,
            mappings);

        var embed = LegacyReactionRoleRetirementComponents.BuildConfirmationEmbed(preview);

        Assert.Equal(5, embed.Fields.Length);
        Assert.All(embed.Fields, field => Assert.InRange(field.Value.Length, 1, 1024));
        Assert.Contains(":emoji25:", embed.Fields[^1].Value, StringComparison.Ordinal);
        Assert.Contains("Role 25", embed.Fields[^1].Value, StringComparison.Ordinal);
    }
}
