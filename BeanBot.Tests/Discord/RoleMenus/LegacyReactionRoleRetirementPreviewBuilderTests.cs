using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class LegacyReactionRoleRetirementPreviewBuilderTests
{
    [Fact]
    public async Task CreateAsync_ShowsReadableNamesForExactSavedPanel()
    {
        var settings = Settings();
        var result = await BuildAsync(settings,
            LegacyReactionRolePanelLookupStatus.Found);

        var preview = Assert.IsType<LegacyReactionRoleRetirementPreview>(result.Preview);
        Assert.Null(result.Issue);
        Assert.Equal("Games", preview.Label);
        Assert.False(preview.SourceWasMissing);
        var mapping = Assert.Single(preview.Mappings);
        Assert.Equal("Gamer", mapping.RoleName);
        Assert.Equal("sparkle", mapping.EmojiName);
        Assert.Equal(LegacyReactionRoleRetirementBinding.Fingerprint(settings),
            preview.Fingerprint);
    }

    [Fact]
    public async Task CreateAsync_AllowsMissingPanelWithReadableFallbacks()
    {
        var result = await BuildAsync(Settings(),
            LegacyReactionRolePanelLookupStatus.MessageMissing,
            _ => null, _ => null);

        var preview = Assert.IsType<LegacyReactionRoleRetirementPreview>(result.Preview);
        Assert.True(preview.SourceWasMissing);
        var mapping = Assert.Single(preview.Mappings);
        Assert.Null(mapping.EmojiName);
        Assert.Null(mapping.RoleName);
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingForeignAndUnrecognizedSources()
    {
        var missing = await BuildAsync(null,
            LegacyReactionRolePanelLookupStatus.Found);
        var foreign = await BuildAsync(new ReactionRoleSettings(
            [new RoleEmotePair("4", "5")], "9", "2", "3"),
            LegacyReactionRolePanelLookupStatus.Found);
        var unsafePanel = await BuildAsync(Settings(),
            LegacyReactionRolePanelLookupStatus.Unrecognized);

        Assert.Equal(LegacyReactionRoleRetirementPreviewIssue.MissingSettings,
            missing.Issue);
        Assert.Equal(LegacyReactionRoleRetirementPreviewIssue.InvalidSavedBinding,
            foreign.Issue);
        Assert.Equal(LegacyReactionRoleRetirementPreviewIssue.UnsafePanel,
            unsafePanel.Issue);
        Assert.Null(missing.Preview);
        Assert.Null(foreign.Preview);
        Assert.Null(unsafePanel.Preview);
    }

    private static ReactionRoleSettings Settings()
        => new([new RoleEmotePair("4", "5")], "1", "2", "3");

    private static Task<LegacyReactionRoleRetirementPreviewResult> BuildAsync(
        ReactionRoleSettings? settings,
        LegacyReactionRolePanelLookupStatus status,
        Func<ulong, string?>? roleName = null,
        Func<ulong, string?>? emojiName = null)
        => LegacyReactionRoleRetirementPreviewBuilder.CreateAsync(
            3, 1, 99,
            (_, _) => Task.FromResult(settings),
            (source, _, _) => Task.FromResult(new LegacyReactionRolePanelLookupResult(
                status, status == LegacyReactionRolePanelLookupStatus.Found
                    ? new LegacyReactionRolePanelSnapshot(
                        source.GuildId, source.ChannelId, source.MessageId, 99, [])
                    : null,
                "Games")),
            roleName ?? (_ => "Gamer"),
            emojiName ?? (_ => "sparkle"),
            CancellationToken.None);
}
