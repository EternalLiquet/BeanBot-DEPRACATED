using System.Globalization;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuPublicationWorkflowTests
{
    private const ulong BotUserId = 90UL;
    private const ulong PanelMessageId = 100UL;

    [Fact]
    public async Task ExecuteAsync_PublishesNewPanelAndPersistsStableDraftMenuId()
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore();

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.Published, result.Status);
        Assert.Equal(PanelMessageId, result.MessageId);
        Assert.Empty(result.Failures);
        Assert.False(result.CanRetry);
        Assert.True(result.IsTerminal);
        Assert.Equal(1, store.SendCount);
        Assert.Equal(1, store.UpsertCount);
        Assert.Equal(0, store.DeleteCount);
        var persisted = Assert.IsType<RoleMenuSettings>(store.Settings);
        Assert.Equal(draft.MenuId, persisted.Id);
        Assert.True(RoleMenuPublicationSettings.Matches(
            persisted,
            draft,
            PanelMessageId));
    }

    [Fact]
    public async Task ExecuteAsync_ReusesExactPersistedPanelWithoutSending()
    {
        var draft = CreateDraft();
        var createdAt = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var settings = RoleMenuPublicationSettings.Create(draft, PanelMessageId);
        settings.CreatedAtUtc = createdAt;
        var store = new FakePublicationStore
        {
            Settings = settings
        };
        store.Panels.Add(CreatePanel(draft));

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.Published, result.Status);
        Assert.Equal(PanelMessageId, result.MessageId);
        Assert.Equal(0, store.SendCount);
        Assert.Equal(1, store.ExactPanelReadCount);
        Assert.Equal(0, store.RecentPanelReadCount);
        Assert.Equal(createdAt, Assert.IsType<RoleMenuSettings>(store.Settings).CreatedAtUtc);
    }

    [Fact]
    public async Task ExecuteAsync_SendCommitThenThrow_ReconcilesPanelAndPublishes()
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore
        {
            ThrowAfterSendCommit = true
        };

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.Published, result.Status);
        Assert.Equal(PanelMessageId, result.MessageId);
        Assert.Equal(1, store.SendCount);
        Assert.Equal(2, store.RecentPanelReadCount);
        Assert.False(store.RecentPanelReadTokens[0].CanBeCanceled);
        Assert.True(store.RecentPanelReadTokens[1].CanBeCanceled);
        Assert.Equal(1, store.UpsertCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(RoleMenuPublicationFailurePhase.PanelPublication, failure.Phase);
        Assert.Same(store.PanelSendFailure, failure.Exception);
    }

    [Fact]
    public async Task ExecuteAsync_UpsertCommitThenThrow_ReconcilesMatchingSettingsAsPublished()
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore
        {
            ThrowAfterUpsertCommit = true
        };

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.Published, result.Status);
        Assert.Equal(PanelMessageId, result.MessageId);
        Assert.Equal(2, store.SettingsReadCount);
        Assert.False(store.SettingsReadTokens[0].CanBeCanceled);
        Assert.True(store.SettingsReadTokens[1].CanBeCanceled);
        Assert.Equal(0, store.DeleteCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(RoleMenuPublicationFailurePhase.Persistence, failure.Phase);
        Assert.Same(store.PersistenceWriteFailure, failure.Exception);
    }

    [Theory]
    [InlineData(
        true,
        (int)RoleMenuPublicationStatus.PersistenceAbsentPanelRolledBack)]
    [InlineData(
        false,
        (int)RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed)]
    public async Task ExecuteAsync_ConfirmedAbsentPersistence_ReportsRollbackOutcome(
        bool rollbackSucceeds,
        int expectedStatus)
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore
        {
            ThrowBeforeUpsertCommit = true,
            DeleteSucceeds = rollbackSucceeds
        };

        var result = await ExecuteAsync(draft, store);

        Assert.Equal((RoleMenuPublicationStatus)expectedStatus, result.Status);
        ulong? expectedMessageId = rollbackSucceeds ? null : PanelMessageId;
        Assert.Equal(expectedMessageId, result.MessageId);
        Assert.Equal(rollbackSucceeds, result.CanRetry);
        Assert.Equal(!rollbackSucceeds, result.IsTerminal);
        Assert.Equal(1, store.DeleteCount);
        var persistenceFailure = Assert.Single(result.Failures);
        Assert.Equal(RoleMenuPublicationFailurePhase.Persistence, persistenceFailure.Phase);
        Assert.Same(store.PersistenceWriteFailure, persistenceFailure.Exception);
        Assert.True(store.DeleteTokens[0].CanBeCanceled);
        Assert.NotEqual(store.SettingsReadTokens[1], store.DeleteTokens[0]);
        if (rollbackSucceeds)
        {
            Assert.Empty(store.Panels);
        }
        else
        {
            Assert.Single(store.Panels);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SendFailureWithoutReconciledPanel_ReturnsUnknownOutcome()
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore
        {
            ThrowBeforeSendCommit = true
        };

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.PanelOutcomeUnknown, result.Status);
        Assert.Null(result.MessageId);
        Assert.False(result.CanRetry);
        Assert.True(result.IsTerminal);
        Assert.Equal(0, store.UpsertCount);
        Assert.Equal(0, store.DeleteCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(RoleMenuPublicationFailurePhase.PanelPublication, failure.Phase);
        Assert.Same(store.PanelSendFailure, failure.Exception);
    }

    [Fact]
    public async Task ExecuteAsync_MismatchedSentPanel_IsNotPersisted()
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore
        {
            SentPanelAuthorId = BotUserId + 1UL
        };

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.PanelOutcomeUnknown, result.Status);
        Assert.Null(result.MessageId);
        Assert.False(result.CanRetry);
        Assert.True(result.IsTerminal);
        Assert.Equal(0, store.UpsertCount);
        Assert.Single(store.Panels);
        Assert.Equal(
            RoleMenuPublicationFailurePhase.PanelPublication,
            Assert.Single(result.Failures).Phase);
    }

    [Fact]
    public async Task ExecuteAsync_PersistenceReconciliationFailure_LeavesKnownPanelInPlace()
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore
        {
            ThrowBeforeUpsertCommit = true,
            ThrowOnReconciliationSettingsRead = true
        };

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.PersistenceOutcomeUnknown, result.Status);
        Assert.Equal(PanelMessageId, result.MessageId);
        Assert.False(result.CanRetry);
        Assert.True(result.IsTerminal);
        Assert.Single(store.Panels);
        Assert.Equal(0, store.DeleteCount);
        Assert.Collection(
            result.Failures,
            failure =>
            {
                Assert.Equal(RoleMenuPublicationFailurePhase.Persistence, failure.Phase);
                Assert.Same(store.PersistenceWriteFailure, failure.Exception);
            },
            failure =>
            {
                Assert.Equal(
                    RoleMenuPublicationFailurePhase.PersistenceReconciliation,
                    failure.Phase);
                Assert.Same(store.PersistenceReadFailure, failure.Exception);
            });
    }

    [Fact]
    public async Task ExecuteAsync_MismatchedReconciledSettings_LeavesKnownPanelInPlace()
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore
        {
            ThrowBeforeUpsertCommit = true,
            ReconciledSettings = new RoleMenuSettings(
                draft.MenuId,
                draft.GuildId.ToString(CultureInfo.InvariantCulture),
                draft.TargetChannelId.ToString(CultureInfo.InvariantCulture),
                PanelMessageId.ToString(CultureInfo.InvariantCulture),
                "Different title",
                draft.Description,
                draft.RoleIds.Select(roleId => roleId.ToString(CultureInfo.InvariantCulture)),
                draft.SelectionMode)
        };

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.PersistenceOutcomeUnknown, result.Status);
        Assert.Equal(PanelMessageId, result.MessageId);
        Assert.False(result.CanRetry);
        Assert.True(result.IsTerminal);
        Assert.Single(store.Panels);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_RollbackException_ReportsFailureAndKeepsPanel()
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore
        {
            ThrowBeforeUpsertCommit = true,
            ThrowOnDelete = true
        };

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(
            RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed,
            result.Status);
        Assert.Equal(PanelMessageId, result.MessageId);
        Assert.False(result.CanRetry);
        Assert.True(result.IsTerminal);
        Assert.Single(store.Panels);
        Assert.Equal(1, store.DeleteCount);
        Assert.Collection(
            result.Failures,
            failure =>
            {
                Assert.Equal(RoleMenuPublicationFailurePhase.Persistence, failure.Phase);
                Assert.Same(store.PersistenceWriteFailure, failure.Exception);
            },
            failure =>
            {
                Assert.Equal(RoleMenuPublicationFailurePhase.PanelRollback, failure.Phase);
                Assert.Same(store.PanelRollbackFailure, failure.Exception);
            });
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateAfterUnknownSendOutcome_ReusesStablePanel()
    {
        var draft = CreateDraft();
        var store = new FakePublicationStore
        {
            ThrowAfterSendCommit = true,
            ThrowOnFirstPanelReconciliation = true
        };

        var first = await ExecuteAsync(draft, store);
        store.ThrowAfterSendCommit = false;
        store.ThrowOnFirstPanelReconciliation = false;
        var retry = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.PanelOutcomeUnknown, first.Status);
        Assert.Collection(
            first.Failures,
            failure =>
            {
                Assert.Equal(RoleMenuPublicationFailurePhase.PanelPublication, failure.Phase);
                Assert.Same(store.PanelSendFailure, failure.Exception);
            },
            failure =>
            {
                Assert.Equal(
                    RoleMenuPublicationFailurePhase.PanelReconciliation,
                    failure.Phase);
                Assert.Same(store.PanelReconciliationFailure, failure.Exception);
            });
        Assert.False(first.CanRetry);
        Assert.True(first.IsTerminal);
        Assert.Equal(RoleMenuPublicationStatus.Published, retry.Status);
        Assert.Equal(PanelMessageId, retry.MessageId);
        Assert.Equal(1, store.SendCount);
        Assert.Single(store.Panels);
        Assert.True(RoleMenuPublicationSettings.Matches(
            Assert.IsType<RoleMenuSettings>(store.Settings),
            draft,
            PanelMessageId));
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresMismatchedExactPanelAndReusesMatchingRecentPanel()
    {
        var draft = CreateDraft();
        var settings = RoleMenuPublicationSettings.Create(draft, 99UL);
        var store = new FakePublicationStore
        {
            Settings = settings
        };
        store.Panels.Add(CreatePanel(draft) with
        {
            MessageId = 99UL,
            AuthorId = BotUserId + 1UL
        });
        store.Panels.Add(CreatePanel(draft));

        var result = await ExecuteAsync(draft, store);

        Assert.Equal(RoleMenuPublicationStatus.Published, result.Status);
        Assert.Equal(PanelMessageId, result.MessageId);
        Assert.Equal(0, store.SendCount);
        Assert.Equal(1, store.ExactPanelReadCount);
        Assert.Equal(1, store.RecentPanelReadCount);
    }

    private static Task<RoleMenuPublicationResult> ExecuteAsync(
        RoleMenuDraft draft,
        FakePublicationStore store)
        => RoleMenuPublicationWorkflow.ExecuteAsync(
            draft,
            BotUserId,
            store.CreateOperations(),
            CancellationToken.None);

    private static RoleMenuDraft CreateDraft()
        => new(
            Guid.NewGuid(),
            ObjectId.GenerateNewId(),
            1UL,
            10UL,
            2UL,
            "Games",
            "Choose games",
            [4UL, 5UL],
            RoleMenuSelectionMode.Multiple,
            DateTimeOffset.UtcNow.AddMinutes(10));

    private static RoleMenuPanelSnapshot CreatePanel(RoleMenuDraft draft)
        => new(
            draft.GuildId,
            draft.TargetChannelId,
            PanelMessageId,
            BotUserId,
            true);

    private sealed class FakePublicationStore
    {
        private int _settingsReadCount;
        private int _recentPanelReadCount;

        internal RoleMenuSettings? Settings { get; set; }
        internal RoleMenuSettings? ReconciledSettings { get; init; }
        internal List<RoleMenuPanelSnapshot> Panels { get; } = [];
        internal List<CancellationToken> SettingsReadTokens { get; } = [];
        internal List<CancellationToken> RecentPanelReadTokens { get; } = [];
        internal List<CancellationToken> DeleteTokens { get; } = [];
        internal bool ThrowBeforeSendCommit { get; init; }
        internal bool ThrowAfterSendCommit { get; set; }
        internal bool ThrowBeforeUpsertCommit { get; init; }
        internal bool ThrowAfterUpsertCommit { get; init; }
        internal bool ThrowOnReconciliationSettingsRead { get; init; }
        internal bool ThrowOnFirstPanelReconciliation { get; set; }
        internal bool ThrowOnDelete { get; init; }
        internal ulong? SentPanelAuthorId { get; init; }
        internal bool DeleteSucceeds { get; init; } = true;
        internal InvalidOperationException PanelSendFailure { get; } =
            new("Panel publication failed.");
        internal InvalidOperationException PanelReconciliationFailure { get; } =
            new("Panel reconciliation failed.");
        internal InvalidOperationException PersistenceWriteFailure { get; } =
            new("Persistence publication failed.");
        internal InvalidOperationException PersistenceReadFailure { get; } =
            new("Persistence reconciliation failed.");
        internal InvalidOperationException PanelRollbackFailure { get; } =
            new("Panel rollback failed.");
        internal int SettingsReadCount => _settingsReadCount;
        internal int ExactPanelReadCount { get; private set; }
        internal int RecentPanelReadCount => _recentPanelReadCount;
        internal int SendCount { get; private set; }
        internal int UpsertCount { get; private set; }
        internal int DeleteCount { get; private set; }

        internal RoleMenuPublicationOperations CreateOperations()
            => new(
                ReadSettingsAsync,
                ReadExactPanelAsync,
                ReadRecentPanelsAsync,
                SendPanelAsync,
                UpsertSettingsAsync,
                DeletePanelAsync);

        private Task<RoleMenuSettings?> ReadSettingsAsync(
            ObjectId menuId,
            ulong guildId,
            CancellationToken cancellationToken)
        {
            _settingsReadCount++;
            SettingsReadTokens.Add(cancellationToken);
            if (_settingsReadCount > 1 && ThrowOnReconciliationSettingsRead)
            {
                return Task.FromException<RoleMenuSettings?>(
                    PersistenceReadFailure);
            }

            if (_settingsReadCount > 1 && ReconciledSettings is not null)
            {
                return Task.FromResult<RoleMenuSettings?>(ReconciledSettings);
            }

            return Task.FromResult(
                Settings is not null
                && Settings.Id == menuId
                && string.Equals(
                    Settings.GuildId,
                    guildId.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                    ? Settings
                    : null);
        }

        private Task<RoleMenuPanelSnapshot?> ReadExactPanelAsync(
            ulong channelId,
            ulong messageId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExactPanelReadCount++;
            return Task.FromResult<RoleMenuPanelSnapshot?>(Panels.FirstOrDefault(
                panel => panel.ChannelId == channelId && panel.MessageId == messageId));
        }

        private Task<IReadOnlyList<RoleMenuPanelSnapshot>> ReadRecentPanelsAsync(
            ulong channelId,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            _recentPanelReadCount++;
            RecentPanelReadTokens.Add(cancellationToken);
            if (_recentPanelReadCount > 1 && ThrowOnFirstPanelReconciliation)
            {
                return Task.FromException<IReadOnlyList<RoleMenuPanelSnapshot>>(
                    PanelReconciliationFailure);
            }

            IReadOnlyList<RoleMenuPanelSnapshot> result =
                [.. Panels
                    .Where(panel => panel.ChannelId == channelId)
                    .Take(maximumResults)];
            return Task.FromResult(result);
        }

        private Task<RoleMenuPanelSnapshot> SendPanelAsync(
            RoleMenuDraft draft,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            if (ThrowBeforeSendCommit)
            {
                return Task.FromException<RoleMenuPanelSnapshot>(
                    PanelSendFailure);
            }

            var panel = CreatePanel(draft) with
            {
                AuthorId = SentPanelAuthorId ?? BotUserId
            };
            Panels.Add(panel);
            return ThrowAfterSendCommit
                ? Task.FromException<RoleMenuPanelSnapshot>(
                    PanelSendFailure)
                : Task.FromResult(panel);
        }

        private Task UpsertSettingsAsync(
            RoleMenuSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpsertCount++;
            if (ThrowBeforeUpsertCommit)
            {
                return Task.FromException(
                    PersistenceWriteFailure);
            }

            Settings = settings;
            return ThrowAfterUpsertCommit
                ? Task.FromException(
                    PersistenceWriteFailure)
                : Task.CompletedTask;
        }

        private Task<bool> DeletePanelAsync(
            RoleMenuPanelSnapshot panel,
            CancellationToken cancellationToken)
        {
            DeleteCount++;
            DeleteTokens.Add(cancellationToken);
            if (ThrowOnDelete)
            {
                return Task.FromException<bool>(
                    PanelRollbackFailure);
            }

            if (DeleteSucceeds)
            {
                Panels.RemoveAll(candidate => candidate.MessageId == panel.MessageId);
            }

            return Task.FromResult(DeleteSucceeds);
        }
    }
}
