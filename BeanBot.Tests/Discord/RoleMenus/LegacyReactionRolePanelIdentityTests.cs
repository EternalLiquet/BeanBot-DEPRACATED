using BeanBot.Discord.RoleMenus;
using Discord;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class LegacyReactionRolePanelIdentityTests
{
    [Fact]
    public void TryRecognize_AcceptsExactBeanBotLegacyPanelAndSuggestsLabel()
    {
        IReadOnlyCollection<Embed> embeds =
        [
            new EmbedBuilder()
                .AddField("😀", "<@&10>", inline: true)
                .AddField("😎", "<@&20>", inline: true)
                .WithFooter("Role Group: Games")
                .Build()
        ];

        var recognized = LegacyReactionRolePanelIdentity.TryRecognize(
            99UL,
            99UL,
            embeds,
            [10UL, 20UL],
            out var title);

        Assert.True(recognized);
        Assert.Equal("Games", title);
    }

    [Fact]
    public void TryRecognize_RejectsWrongAuthorOrPanelShape()
    {
        IReadOnlyCollection<Embed> embeds =
        [
            new EmbedBuilder()
                .AddField("😀", "<@&10>", inline: true)
                .WithFooter("Role Group: Games")
                .Build()
        ];

        Assert.False(LegacyReactionRolePanelIdentity.TryRecognize(
            98UL,
            99UL,
            embeds,
            [10UL],
            out _));
        Assert.False(LegacyReactionRolePanelIdentity.TryRecognize(
            99UL,
            99UL,
            embeds,
            [10UL, 20UL],
            out _));
    }

    [Fact]
    public void TryRecognize_RejectsNonLegacyFooterAndDuplicateExpectedRoles()
    {
        IReadOnlyCollection<Embed> embeds =
        [
            new EmbedBuilder()
                .AddField("😀", "<@&10>", inline: true)
                .WithFooter("Role menu: Games")
                .Build()
        ];

        Assert.False(LegacyReactionRolePanelIdentity.TryRecognize(
            99UL,
            99UL,
            embeds,
            [10UL],
            out _));
        Assert.False(LegacyReactionRolePanelIdentity.TryRecognize(
            99UL,
            99UL,
            [new EmbedBuilder()
                .AddField("😀", "<@&10>", inline: true)
                .WithFooter("Role Group: Games")
                .Build()],
            [10UL, 10UL],
            out _));
    }
}
