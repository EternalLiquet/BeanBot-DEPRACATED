using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Discord;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class LegacyReactionRoleRetirementPickerTests
{
    [Fact]
    public void Build_ShowsBoundedReadableOptionsAndNextPage()
    {
        var settings = Enumerable.Range(1, 26)
            .Select(index => new ReactionRoleSettings(
                [new RoleEmotePair("4", "5")], "1", "2", index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            { Id = ObjectId.GenerateNewId() })
            .Reverse().ToList();

        var components = LegacyReactionRoleRetirementPicker.Build(9, settings, null,
            newer: false, _ => "general", _ => "Gamer");
        var rows = components.Components.OfType<ActionRowComponent>().ToArray();
        var selector = Assert.Single(rows[0].Components.OfType<SelectMenuComponent>());
        var next = Assert.Single(rows[1].Components.OfType<ButtonComponent>());

        Assert.Equal(25, selector.Options.Count);
        Assert.All(selector.Options, option =>
        {
            Assert.Contains("#general", option.Label, StringComparison.Ordinal);
            Assert.Contains("Gamer", option.Description, StringComparison.Ordinal);
            Assert.DoesNotContain("emoji ID", option.Description, StringComparison.Ordinal);
        });
        Assert.Equal(LegacyReactionRoleRetirementPicker.Page(9, false,
            settings[24].Id), next.CustomId);
    }

    [Fact]
    public void Build_UsesReadableFallbacksForMissingChannelAndRole()
    {
        var setting = new ReactionRoleSettings(
            [new RoleEmotePair("4", "5")], "1", "2", "3")
        { Id = ObjectId.GenerateNewId() };
        var components = LegacyReactionRoleRetirementPicker.Build(9, [setting], null,
            newer: false, _ => null, _ => null);
        var option = components.Components.OfType<ActionRowComponent>()
            .SelectMany(row => row.Components).OfType<SelectMenuComponent>()
            .Single().Options.Single();

        Assert.Contains("deleted channel", option.Label, StringComparison.Ordinal);
        Assert.Equal("Saved legacy role panel", option.Description);
    }
}
