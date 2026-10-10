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
    public void Cursor_RejectsMalformedAndSupportsBothDirections()
    {
        var cursor = ObjectId.GenerateNewId();
        Assert.True(LegacyReactionRoleRetirementPicker.TryParseCursor("o",
            cursor.ToString(), out var older, out var parsed));
        Assert.False(older);
        Assert.Equal(cursor, parsed);
        Assert.True(LegacyReactionRoleRetirementPicker.TryParseCursor("n",
            cursor.ToString(), out var newer, out parsed));
        Assert.True(newer);
        Assert.False(LegacyReactionRoleRetirementPicker.TryParseCursor("x",
            cursor.ToString(), out _, out _));
        Assert.False(LegacyReactionRoleRetirementPicker.TryParseCursor("o",
            "not-an-id", out _, out _));
    }

    [Fact]
    public void NewerPage_SkipsOverflowBoundaryAndKeepsNavigation()
    {
        var fetched = Enumerable.Range(1, 26)
            .Select(index => new ReactionRoleSettings([], "1", "2",
                index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            { Id = ObjectId.GenerateNewId() })
            .Reverse().ToArray();
        var components = LegacyReactionRoleRetirementPicker.Build(9, fetched,
            fetched[^1].Id, newer: true, _ => "general", _ => null);
        var rows = components.Components.OfType<ActionRowComponent>().ToArray();
        var selector = Assert.Single(rows[0].Components.OfType<SelectMenuComponent>());
        var buttons = rows[1].Components.OfType<ButtonComponent>().ToArray();

        Assert.Equal(25, selector.Options.Count);
        Assert.Equal(fetched[1].MessageId, selector.Options.First().Value);
        Assert.Equal(["Previous", "Next"], buttons.Select(button => button.Label));
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
