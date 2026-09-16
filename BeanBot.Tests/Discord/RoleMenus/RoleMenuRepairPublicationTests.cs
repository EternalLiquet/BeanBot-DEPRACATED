using System.Globalization;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuRepairPublicationTests
{
    private const ulong GuildId = 10UL;
    private const ulong SavedChannelId = 20UL;
    private const ulong SavedMessageId = 30UL;
    private const ulong TargetChannelId = 40UL;
    private const ulong ReplacementMessageId = 50UL;
    private const ulong BotUserId = 60UL;

    [Fact]
    public async Task PublishAsync_ExistingReplacementWithStableMenuId_IsReusedWithoutDuplicateSend()
    {
        var settings = CreateSavedSettings();
        var createdAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        settings.CreatedAtUtc = createdAt;
        Assert.True(RoleMenuSettingsParser.TryParse(settings, out var parsed, out _));
        var draft = RoleMenuRepairWorkflow.CreateRepairDraft(
            settings,
            parsed,
            administratorId: 70UL,
            targetChannelId: TargetChannelId);
        RoleMenuSettings? stored = settings;
        var sendCount = 0;
        var exactReadCount = 0;
        var recentReadCount = 0;
        var replacement = CreateReplacementPanel(settings.Id);

        var result = await RoleMenuPublicationWorkflow.ExecuteAsync(
            draft,
            BotUserId,
            new RoleMenuPublicationOperations(
                (_, _, _) => Task.FromResult<RoleMenuSettings?>(stored),
                (_, _, _) =>
                {
                    exactReadCount++;
                    return Task.FromResult<RoleMenuPanelSnapshot?>(null);
                },
                (_, _, _) =>
                {
                    recentReadCount++;
                    return Task.FromResult<IReadOnlyList<RoleMenuPanelSnapshot>>([replacement]);
                },
                (_, _) =>
                {
                    sendCount++;
                    return Task.FromResult(replacement);
                },
                (updated, _) =>
                {
                    stored = updated;
                    return Task.CompletedTask;
                },
                (_, _) => Task.FromResult(true)),
            CancellationToken.None);

        Assert.Equal(RoleMenuPublicationStatus.Published, result.Status);
        Assert.Equal(ReplacementMessageId, result.MessageId);
        Assert.Equal(0, sendCount);
        Assert.Equal(0, exactReadCount);
        Assert.Equal(1, recentReadCount);
        var repaired = Assert.IsType<RoleMenuSettings>(stored);
        Assert.Equal(settings.Id, repaired.Id);
        Assert.Equal(TargetChannelId.ToString(CultureInfo.InvariantCulture), repaired.ChannelId);
        Assert.Equal(ReplacementMessageId.ToString(CultureInfo.InvariantCulture), repaired.MessageId);
        Assert.Equal(settings.Title, repaired.Title);
        Assert.Equal(settings.Description, repaired.Description);
        Assert.Equal(settings.RoleIds, repaired.RoleIds);
        Assert.Equal(createdAt, repaired.CreatedAtUtc);
    }

    [Fact]
    public async Task PublishAsync_AmbiguousSendThatActuallyCommitted_ReconcilesWithoutSecondSend()
    {
        var settings = CreateSavedSettings();
        Assert.True(RoleMenuSettingsParser.TryParse(settings, out var parsed, out _));
        var draft = RoleMenuRepairWorkflow.CreateRepairDraft(
            settings,
            parsed,
            administratorId: 70UL,
            targetChannelId: TargetChannelId);
        RoleMenuSettings? stored = settings;
        RoleMenuPanelSnapshot? replacement = null;
        var sendCount = 0;
        var recentReadCount = 0;

        var result = await RoleMenuPublicationWorkflow.ExecuteAsync(
            draft,
            BotUserId,
            new RoleMenuPublicationOperations(
                (_, _, _) => Task.FromResult<RoleMenuSettings?>(stored),
                (_, _, _) => Task.FromResult<RoleMenuPanelSnapshot?>(null),
                (_, _, _) =>
                {
                    recentReadCount++;
                    IReadOnlyList<RoleMenuPanelSnapshot> panels = replacement is null
                        ? []
                        : [replacement];
                    return Task.FromResult(panels);
                },
                (_, _) =>
                {
                    sendCount++;
                    replacement = CreateReplacementPanel(settings.Id);
                    return Task.FromException<RoleMenuPanelSnapshot>(
                        new TimeoutException("send outcome unknown"));
                },
                (updated, _) =>
                {
                    stored = updated;
                    return Task.CompletedTask;
                },
                (_, _) => Task.FromResult(true)),
            CancellationToken.None);

        Assert.Equal(RoleMenuPublicationStatus.Published, result.Status);
        Assert.Equal(ReplacementMessageId, result.MessageId);
        Assert.Equal(1, sendCount);
        Assert.Equal(2, recentReadCount);
        Assert.Equal(
            RoleMenuPublicationFailurePhase.PanelPublication,
            Assert.Single(result.Failures).Phase);
        Assert.Equal(
            ReplacementMessageId.ToString(CultureInfo.InvariantCulture),
            Assert.IsType<RoleMenuSettings>(stored).MessageId);
    }

    [Fact]
    public async Task PublishAsync_PersistenceFailureWithOldBinding_KeepsReplacementForSafeRerun()
    {
        var settings = CreateSavedSettings();
        Assert.True(RoleMenuSettingsParser.TryParse(settings, out var parsed, out _));
        var draft = RoleMenuRepairWorkflow.CreateRepairDraft(
            settings,
            parsed,
            administratorId: 70UL,
            targetChannelId: TargetChannelId);
        RoleMenuSettings? stored = settings;
        var replacement = CreateReplacementPanel(settings.Id);
        var deleteCount = 0;

        var result = await RoleMenuPublicationWorkflow.ExecuteAsync(
            draft,
            BotUserId,
            new RoleMenuPublicationOperations(
                (_, _, _) => Task.FromResult<RoleMenuSettings?>(stored),
                (_, _, _) => Task.FromResult<RoleMenuPanelSnapshot?>(null),
                (_, _, _) => Task.FromResult<IReadOnlyList<RoleMenuPanelSnapshot>>([]),
                (_, _) => Task.FromResult(replacement),
                (_, _) => Task.FromException(new InvalidOperationException("mongo write failed")),
                (_, _) =>
                {
                    deleteCount++;
                    return Task.FromResult(true);
                }),
            CancellationToken.None);

        Assert.Equal(RoleMenuPublicationStatus.PersistenceOutcomeUnknown, result.Status);
        Assert.Equal(ReplacementMessageId, result.MessageId);
        Assert.Equal(0, deleteCount);
        Assert.Same(settings, stored);
        Assert.Equal(SavedChannelId.ToString(CultureInfo.InvariantCulture), stored!.ChannelId);
        Assert.Collection(
            result.Failures,
            failure => Assert.Equal(RoleMenuPublicationFailurePhase.Persistence, failure.Phase));
    }

    private static RoleMenuSettings CreateSavedSettings()
        => new(
            ObjectId.GenerateNewId(),
            GuildId.ToString(CultureInfo.InvariantCulture),
            SavedChannelId.ToString(CultureInfo.InvariantCulture),
            SavedMessageId.ToString(CultureInfo.InvariantCulture),
            "Game Roles",
            "Choose games",
            ["100", "101"],
            RoleMenuSelectionMode.Multiple);

    private static RoleMenuPanelSnapshot CreateReplacementPanel(ObjectId menuId)
    {
        _ = menuId;
        return new RoleMenuPanelSnapshot(
            GuildId,
            TargetChannelId,
            ReplacementMessageId,
            BotUserId,
            true);
    }
}
