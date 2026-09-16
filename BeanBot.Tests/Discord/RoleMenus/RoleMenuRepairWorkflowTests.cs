using System.Globalization;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuRepairWorkflowTests
{
    private const ulong GuildId = 10UL;
    private const ulong ChannelId = 20UL;
    private const ulong MessageId = 30UL;
    private const ulong BotUserId = 40UL;

    [Fact]
    public async Task InspectAsync_MissingMessage_IsEligibleForRepair()
    {
        var settings = CreateSettings();

        var result = await InspectAsync(
            settings,
            new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing));

        Assert.Equal(RoleMenuRepairInspectionStatus.Eligible, result.Status);
        Assert.Equal(RoleMenuRepairPanelIssue.MessageMissing, result.PanelIssue);
        Assert.Same(settings, result.Settings);
        Assert.Equal(MessageId, Assert.IsType<ParsedRoleMenuSettings>(result.ParsedSettings).MessageId);
    }

    [Fact]
    public async Task InspectAsync_MissingChannel_IsEligibleButDistinguishedFromMissingMessage()
    {
        var result = await InspectAsync(
            CreateSettings(),
            new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.ChannelMissing));

        Assert.Equal(RoleMenuRepairInspectionStatus.Eligible, result.Status);
        Assert.Equal(RoleMenuRepairPanelIssue.ChannelMissing, result.PanelIssue);
    }

    [Fact]
    public async Task InspectAsync_HealthyPanel_DoesNotAllowReplacement()
    {
        var result = await InspectAsync(
            CreateSettings(),
            new RoleMenuPanelLookupResult(
                RoleMenuPanelLookupStatus.Found,
                new RoleMenuPanelSnapshot(
                    GuildId,
                    ChannelId,
                    MessageId,
                    BotUserId,
                    true)));

        Assert.Equal(RoleMenuRepairInspectionStatus.Healthy, result.Status);
        Assert.False(result.IsEligible);
        Assert.Equal(RoleMenuRepairPanelIssue.None, result.PanelIssue);
    }

    [Theory]
    [InlineData(false, 10UL, 20UL, 30UL, 41UL, (int)RoleMenuRepairPanelIssue.UnexpectedAuthor)]
    [InlineData(false, 10UL, 20UL, 30UL, 40UL, (int)RoleMenuRepairPanelIssue.MissingManageButton)]
    [InlineData(true, 11UL, 20UL, 30UL, 40UL, (int)RoleMenuRepairPanelIssue.GuildMismatch)]
    [InlineData(true, 10UL, 21UL, 30UL, 40UL, (int)RoleMenuRepairPanelIssue.ChannelMismatch)]
    [InlineData(true, 10UL, 20UL, 31UL, 40UL, (int)RoleMenuRepairPanelIssue.MessageMismatch)]
    public async Task InspectAsync_MismatchedLiveMessage_FailsSafe(
        bool hasManageButton,
        ulong panelGuildId,
        ulong panelChannelId,
        ulong panelMessageId,
        ulong panelAuthorId,
        int expectedIssue)
    {
        var result = await InspectAsync(
            CreateSettings(),
            new RoleMenuPanelLookupResult(
                RoleMenuPanelLookupStatus.Found,
                new RoleMenuPanelSnapshot(
                    panelGuildId,
                    panelChannelId,
                    panelMessageId,
                    panelAuthorId,
                    hasManageButton)));

        Assert.Equal(RoleMenuRepairInspectionStatus.UnexpectedPanel, result.Status);
        Assert.False(result.IsEligible);
        Assert.Equal((RoleMenuRepairPanelIssue)expectedIssue, result.PanelIssue);
    }

    [Fact]
    public async Task InspectAsync_InvalidSavedSettings_DoesNotReadDiscord()
    {
        var settings = CreateSettings(messageId: "not-a-snowflake");
        var panelReads = 0;

        var result = await RoleMenuRepairWorkflow.InspectAsync(
            settings.Id,
            GuildId,
            BotUserId,
            (_, _, _) => Task.FromResult<RoleMenuSettings?>(settings),
            (_, _, _, _, _) =>
            {
                panelReads++;
                return Task.FromResult(
                    new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing));
            },
            CancellationToken.None);

        Assert.Equal(RoleMenuRepairInspectionStatus.SettingsInvalid, result.Status);
        Assert.Equal(0, panelReads);
    }

    [Fact]
    public async Task InspectAsync_TransientDiscordFailure_PropagatesBeforeAnyRepairCanPublish()
    {
        var settings = CreateSettings();
        var failure = new InvalidOperationException("discord unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoleMenuRepairWorkflow.InspectAsync(
                settings.Id,
                GuildId,
                BotUserId,
                (_, _, _) => Task.FromResult<RoleMenuSettings?>(settings),
                (_, _, _, _, _) => Task.FromException<RoleMenuPanelLookupResult>(failure),
                CancellationToken.None));

        Assert.Same(failure, exception);
    }

    [Fact]
    public void CreateRepairDraft_PreservesConfigurationAndStableMenuIdButUsesReplacementChannel()
    {
        var settings = CreateSettings();
        settings.CreatedAtUtc = new DateTime(2026, 9, 16, 13, 0, 0, DateTimeKind.Utc);
        Assert.True(RoleMenuSettingsParser.TryParse(settings, out var parsed, out _));

        var draft = RoleMenuRepairWorkflow.CreateRepairDraft(
            settings,
            parsed,
            administratorId: 50UL,
            targetChannelId: 99UL);

        Assert.Equal(settings.Id, draft.MenuId);
        Assert.Equal(GuildId, draft.GuildId);
        Assert.Equal(99UL, draft.TargetChannelId);
        Assert.Equal(settings.Title, draft.Title);
        Assert.Equal(settings.Description, draft.Description);
        Assert.Equal([100UL, 101UL], draft.RoleIds);
        Assert.Equal(settings.SelectionMode, draft.SelectionMode);
    }

    private static Task<RoleMenuRepairInspectionResult> InspectAsync(
        RoleMenuSettings settings,
        RoleMenuPanelLookupResult lookup)
        => RoleMenuRepairWorkflow.InspectAsync(
            settings.Id,
            GuildId,
            BotUserId,
            (_, _, _) => Task.FromResult<RoleMenuSettings?>(settings),
            (_, expectedMenuId, channelId, messageId, _) =>
            {
                Assert.Equal(settings.Id, expectedMenuId);
                Assert.Equal(ChannelId, channelId);
                Assert.Equal(MessageId, messageId);
                return Task.FromResult(lookup);
            },
            CancellationToken.None);

    private static RoleMenuSettings CreateSettings(string? messageId = null)
        => new(
            ObjectId.GenerateNewId(),
            GuildId.ToString(CultureInfo.InvariantCulture),
            ChannelId.ToString(CultureInfo.InvariantCulture),
            messageId ?? MessageId.ToString(CultureInfo.InvariantCulture),
            "Game Roles",
            "Choose games",
            ["100", "101"],
            RoleMenuSelectionMode.Multiple);
}
