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
            "Members keep the roles they already have.",
            embed.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPanelPreview_UsesPlainFallbackTitleAndExplainsSavedSettings()
    {
        var preview = new LegacyReactionRoleRetirementPreview(
            new LegacyReactionRoleSource(1, 2, 3, [4]), null, true,
            [new LegacyReactionRoleRetirementMapping(4, "5")]);

        var embed = LegacyReactionRoleRetirementComponents.BuildConfirmationEmbed(preview);

        Assert.Contains("Unlabeled legacy panel", embed.Description,
            StringComparison.Ordinal);
        Assert.Contains("message or channel is gone", embed.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfirmationEmbed_MissingCustomEmojiUsesNameFallbackWithoutRawId()
    {
        var preview = new LegacyReactionRoleRetirementPreview(
            new LegacyReactionRoleSource(1, 2, 3, [4]),
            "Games", false,
            [new LegacyReactionRoleRetirementMapping(4, "123456789", null, null)]);

        var embed = LegacyReactionRoleRetirementComponents.BuildConfirmationEmbed(preview);
        var text = Assert.Single(embed.Fields).Value;

        Assert.Contains("Custom emoji unavailable → Deleted role", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResult_GivesSafeNextStepForEveryOutcome()
    {
        foreach (var status in Enum.GetValues<LegacyReactionRoleRetirementStatus>())
        {
            var result = LegacyReactionRoleRetirementComponents.FormatResult(new(status));
            Assert.False(string.IsNullOrWhiteSpace(result));
            Assert.DoesNotContain("message ID", result, StringComparison.OrdinalIgnoreCase);
            if (status is not LegacyReactionRoleRetirementStatus.Retired)
            {
                Assert.Contains("/role-menu retire-legacy", result,
                    StringComparison.Ordinal);
            }
        }
        Assert.Contains("missing legacy panel",
            LegacyReactionRoleRetirementComponents.FormatResult(new(
                LegacyReactionRoleRetirementStatus.Retired, SourceWasMissing: true)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousPanelDeletion_DoesNotClaimSavedSettingsRemain()
    {
        var result = LegacyReactionRoleRetirementComponents.FormatResult(new(
            LegacyReactionRoleRetirementStatus.PanelOutcomeUnknown));

        Assert.Contains("Check Discord", result, StringComparison.Ordinal);
        Assert.DoesNotContain("saved settings are still there", result,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfirmationComponents_UseExactUserMessageExpiryAndFingerprint()
    {
        var components = LegacyReactionRoleRetirementComponents.BuildConfirmationComponents(
            9, 3, 1_800_000_000, "1234567890ABCDEF");
        var buttons = components.Components.OfType<global::Discord.ActionRowComponent>()
            .SelectMany(row => row.Components).OfType<global::Discord.ButtonComponent>().ToArray();

        Assert.Equal(2, buttons.Length);
        Assert.Equal(LegacyReactionRoleRetirementCustomIds.Confirm(
            9, 3, 1_800_000_000, "1234567890ABCDEF"), buttons[0].CustomId);
        Assert.Equal(LegacyReactionRoleRetirementCustomIds.Cancel(
            9, 3, 1_800_000_000, "1234567890ABCDEF"), buttons[1].CustomId);
    }

    [Fact]
    public void ConfirmationIds_RejectOversizedCustomId()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LegacyReactionRoleRetirementCustomIds.Confirm(
                9, 3, 1_800_000_000, new string('A', 101)));
    }

    [Fact]
    public void FormatResult_RejectsUnknownStatus()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LegacyReactionRoleRetirementComponents.FormatResult(
                new LegacyReactionRoleRetirementResult(
                    (LegacyReactionRoleRetirementStatus)999)));
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
