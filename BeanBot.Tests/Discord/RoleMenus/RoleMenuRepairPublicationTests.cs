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
        var replacement = CreateReplacementPanel();

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
        Assert.Equal(GuildId.ToString(CultureInfo.InvariantCulture), repaired.GuildId);
        Assert.Equal(TargetChannelId.ToString(CultureInfo.InvariantCulture), repaired.ChannelId);
        Assert.Equal(ReplacementMessageId.ToString(CultureInfo.InvariantCulture), repaired.MessageId);
        Assert.Equal(settings.Title, repaired.Title);
        Assert.Equal(settings.Description, repaired.Description);
        Assert.Equal(settings.RoleIds, repaired.RoleIds);
        Assert.Equal(settings.SelectionMode, repaired.SelectionMode);
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
                    replacement = CreateReplacementPanel();
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
        var replacement = CreateReplacementPanel();
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

    [Fact]
    public async Task PublishAsync_RerunAfterPersistenceFailure_ReusesReplacementInsteadOfSendingAgain()
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
        var upsertCount = 0;
        var operations = new RoleMenuPublicationOperations(
            (_, _, _) => Task.FromResult<RoleMenuSettings?>(stored),
            (_, messageId, _) => Task.FromResult<RoleMenuPanelSnapshot?>(
                replacement is { } panel && panel.MessageId == messageId ? panel : null),
            (_, _, _) => Task.FromResult<IReadOnlyList<RoleMenuPanelSnapshot>>(
                replacement is null ? [] : [replacement]),
            (_, _) =>
            {
                sendCount++;
                replacement = CreateReplacementPanel();
                return Task.FromResult(replacement);
            },
            (updated, _) =>
            {
                upsertCount++;
                if (upsertCount == 1)
                {
                    return Task.FromException(new InvalidOperationException("mongo write failed"));
                }

                stored = updated;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult(true));

        var first = await RoleMenuPublicationWorkflow.ExecuteAsync(
            draft,
            BotUserId,
            operations,
            CancellationToken.None);
        var second = await RoleMenuPublicationWorkflow.ExecuteAsync(
            draft,
            BotUserId,
            operations,
            CancellationToken.None);

        Assert.Equal(RoleMenuPublicationStatus.PersistenceOutcomeUnknown, first.Status);
        Assert.Equal(RoleMenuPublicationStatus.Published, second.Status);
        Assert.Equal(1, sendCount);
        Assert.Equal(ReplacementMessageId, second.MessageId);
        Assert.Equal(
            ReplacementMessageId.ToString(CultureInfo.InvariantCulture),
            Assert.IsType<RoleMenuSettings>(stored).MessageId);
    }

    [Fact]
    public async Task ConcurrentRepairPublications_SerializeToOneReplacementPanel()
    {
        var settings = CreateSavedSettings();
        Assert.True(RoleMenuSettingsParser.TryParse(settings, out var parsed, out _));
        var draft = RoleMenuRepairWorkflow.CreateRepairDraft(
            settings,
            parsed,
            administratorId: 70UL,
            targetChannelId: TargetChannelId);
        var coordinator = new RoleMenuMutationCoordinator();
        RoleMenuSettings? stored = settings;
        RoleMenuPanelSnapshot? replacement = null;
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var operations = new RoleMenuPublicationOperations(
            (_, _, _) => Task.FromResult<RoleMenuSettings?>(stored),
            (_, messageId, _) => Task.FromResult<RoleMenuPanelSnapshot?>(
                replacement is { } panel && panel.MessageId == messageId ? panel : null),
            (_, _, _) => Task.FromResult<IReadOnlyList<RoleMenuPanelSnapshot>>(
                replacement is null ? [] : [replacement]),
            async (_, _) =>
            {
                sendCount++;
                sendStarted.SetResult();
                await releaseSend.Task;
                replacement = CreateReplacementPanel();
                return replacement;
            },
            (updated, _) =>
            {
                stored = updated;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult(true));

        Task<RoleMenuPublicationResult> RunRepairAsync()
            => coordinator.RunMenuWriteAsync(
                settings.Id.ToString(),
                operationToken => RoleMenuPublicationWorkflow.ExecuteAsync(
                    draft,
                    BotUserId,
                    operations,
                    operationToken),
                CancellationToken.None);

        var first = RunRepairAsync();
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = RunRepairAsync();
        await Task.Yield();
        Assert.Equal(1, sendCount);

        releaseSend.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(RoleMenuPublicationStatus.Published, result.Status));
        Assert.Equal(1, sendCount);
        Assert.Equal(
            ReplacementMessageId.ToString(CultureInfo.InvariantCulture),
            Assert.IsType<RoleMenuSettings>(stored).MessageId);
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

    private static RoleMenuPanelSnapshot CreateReplacementPanel()
        => new(
            GuildId,
            TargetChannelId,
            ReplacementMessageId,
            BotUserId,
            true);
}
