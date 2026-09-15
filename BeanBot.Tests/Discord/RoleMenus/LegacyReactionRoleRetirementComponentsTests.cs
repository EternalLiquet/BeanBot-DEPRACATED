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
                new LegacyReactionRoleRetirementMapping(4, "5"),
                new LegacyReactionRoleRetirementMapping(6, "7")
            ]);

        var embed = LegacyReactionRoleRetirementComponents.BuildConfirmationEmbed(preview);
        var mappingText = string.Join("\n", embed.Fields.Select(field => field.Value));

        Assert.Contains("`5` → <@&4>", mappingText, StringComparison.Ordinal);
        Assert.Contains("`7` → <@&6>", mappingText, StringComparison.Ordinal);
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
                (200 + index).ToString(System.Globalization.CultureInfo.InvariantCulture)))
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
        Assert.Contains("225", embed.Fields[^1].Value, StringComparison.Ordinal);
        Assert.Contains("<@&125>", embed.Fields[^1].Value, StringComparison.Ordinal);
    }
}
