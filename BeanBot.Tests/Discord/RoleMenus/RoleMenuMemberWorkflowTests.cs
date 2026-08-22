using System.Globalization;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuMemberWorkflowTests
{
    private const ulong GuildId = 101UL;
    private const ulong BotUserId = 202UL;
    private const ulong MemberUserId = 303UL;
    private const ulong ChannelId = 404UL;
    private const ulong MessageId = 505UL;
    private const ulong FirstRoleId = 10UL;
    private const ulong SecondRoleId = 11UL;
    private const ulong ThirdRoleId = 12UL;
    private const ulong UnconfiguredRoleId = 99UL;
    private static readonly ObjectId MenuId =
        ObjectId.Parse("64e7611aaac75f172f0f1234");

    public static TheoryData<string[], int, int>
        InvalidSelections
        => new()
        {
            {
                ["not-a-role"],
                (int)RoleMenuSelectionMode.Multiple,
                (int)RoleMenuSelectionIssue.InvalidSelectedRole
            },
            {
                ["999"],
                (int)RoleMenuSelectionMode.Multiple,
                (int)RoleMenuSelectionIssue.RoleNotAllowed
            },
            {
                ["10", "10"],
                (int)RoleMenuSelectionMode.Multiple,
                (int)RoleMenuSelectionIssue.DuplicateSelectedRole
            },
            {
                ["10", "11"],
                (int)RoleMenuSelectionMode.Exclusive,
                (int)RoleMenuSelectionIssue.TooManySelections
            }
        };

    [Fact]
    public async Task ExecuteAsync_ValidMultipleSelection_MutatesEveryPlannedRoleAndReconciles()
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId, SecondRoleId, ThirdRoleId]),
            [FirstRoleId, UnconfiguredRoleId]);
        using var operationCancellation = new CancellationTokenSource();

        var result = await ExecuteAsync(
            fake,
            [
                SecondRoleId.ToString(CultureInfo.InvariantCulture),
                ThirdRoleId.ToString(CultureInfo.InvariantCulture)
            ],
            cancellationToken: operationCancellation.Token);

        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedComplete, result.Status);
        Assert.True(result.IsConfirmed);
        Assert.True(result.IsComplete);
        Assert.Equal(
            ["add:11", "add:12", "remove:10"],
            fake.MutationCalls);
        Assert.Equal(
            [SecondRoleId, ThirdRoleId, UnconfiguredRoleId],
            fake.MemberRoleIds.Order());
        var reconciliation = Assert.IsType<RoleMenuSelectionReconciliation>(
            result.Reconciliation);
        Assert.Equal([SecondRoleId, ThirdRoleId], reconciliation.AddedRoleIds);
        Assert.Equal([FirstRoleId], reconciliation.RemovedRoleIds);
        Assert.Empty(reconciliation.MissingSelectedRoleIds);
        Assert.Empty(reconciliation.StillAssignedUnselectedRoleIds);
        Assert.Equal(2, fake.MemberReadTokens.Count);
        Assert.Equal(operationCancellation.Token, fake.MemberReadTokens[0]);
        Assert.NotEqual(operationCancellation.Token, fake.MemberReadTokens[1]);
        Assert.True(fake.MemberReadTokens[1].CanBeCanceled);
        Assert.False(fake.MemberReadTokens[1].IsCancellationRequested);
        Assert.Equal(MenuId, fake.PanelReadMenuIds.Single());
        Assert.Equal(ChannelId, fake.PanelReadLocations.Single().ChannelId);
        Assert.Equal(MessageId, fake.PanelReadLocations.Single().MessageId);
    }

    [Fact]
    public async Task ExecuteAsync_ExclusiveSelection_AddsReplacementBeforeRemovingOldRole()
    {
        var fake = new RecordingOperations(
            CreateSettings(
                [FirstRoleId, SecondRoleId],
                RoleMenuSelectionMode.Exclusive),
            [FirstRoleId, UnconfiguredRoleId]);

        var result = await ExecuteAsync(fake, ["11"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedComplete, result.Status);
        Assert.Equal(["add:11", "remove:10"], fake.MutationCalls);
        Assert.Equal([SecondRoleId, UnconfiguredRoleId], fake.MemberRoleIds.Order());
    }

    [Fact]
    public async Task ExecuteAsync_ExclusiveAddFailure_SkipsRemovalAndReportsObservedIncompleteState()
    {
        var expected = new InvalidOperationException("add rejected");
        var fake = new RecordingOperations(
            CreateSettings(
                [FirstRoleId, SecondRoleId],
                RoleMenuSelectionMode.Exclusive),
            [FirstRoleId, UnconfiguredRoleId])
        {
            AddRoleHandler = (_, _) => Task.FromException(expected)
        };

        var result = await ExecuteAsync(fake, ["11"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedIncomplete, result.Status);
        Assert.True(result.IsConfirmed);
        Assert.False(result.IsComplete);
        Assert.Equal(["add:11"], fake.MutationCalls);
        var synchronization = Assert.IsType<RoleMenuSynchronizationResult>(
            result.Synchronization);
        Assert.Equal([FirstRoleId], synchronization.SkippedRemovalRoleIds);
        Assert.Same(expected, Assert.Single(synchronization.Failures).Exception);
        var reconciliation = Assert.IsType<RoleMenuSelectionReconciliation>(
            result.Reconciliation);
        Assert.Equal([SecondRoleId], reconciliation.MissingSelectedRoleIds);
        Assert.Equal([FirstRoleId], reconciliation.StillAssignedUnselectedRoleIds);
    }

    [Fact]
    public async Task ExecuteAsync_Clear_RemovesOnlyConfiguredRoles()
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId, SecondRoleId]),
            [FirstRoleId, SecondRoleId, UnconfiguredRoleId]);

        var result = await ExecuteAsync(fake, []);

        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedComplete, result.Status);
        Assert.Equal(["remove:10", "remove:11"], fake.MutationCalls);
        Assert.Equal([UnconfiguredRoleId], fake.MemberRoleIds);
        var reconciliation = Assert.IsType<RoleMenuSelectionReconciliation>(
            result.Reconciliation);
        Assert.Equal([FirstRoleId, SecondRoleId], reconciliation.RemovedRoleIds);
    }

    [Fact]
    public async Task ExecuteAsync_NoOp_StillPerformsOneBoundedFreshFinalRead()
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId, SecondRoleId]),
            [FirstRoleId, UnconfiguredRoleId]);

        var result = await ExecuteAsync(fake, ["10"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedComplete, result.Status);
        Assert.Empty(fake.MutationCalls);
        Assert.Equal(2, fake.MemberReadTokens.Count);
        Assert.True(fake.MemberReadTokens[1].CanBeCanceled);
        var reconciliation = Assert.IsType<RoleMenuSelectionReconciliation>(
            result.Reconciliation);
        Assert.Empty(reconciliation.AddedRoleIds);
        Assert.Empty(reconciliation.RemovedRoleIds);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedSubmission_IsIdempotentAndRechecksBothAttempts()
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId, SecondRoleId]),
            [FirstRoleId, UnconfiguredRoleId]);

        var first = await ExecuteAsync(fake, ["11"]);
        var second = await ExecuteAsync(fake, ["11"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedComplete, first.Status);
        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedComplete, second.Status);
        Assert.Equal(["add:11", "remove:10"], fake.MutationCalls);
        Assert.Equal(4, fake.MemberReadTokens.Count);
        Assert.NotEqual(fake.MemberReadTokens[1], fake.MemberReadTokens[3]);
        var secondReconciliation = Assert.IsType<RoleMenuSelectionReconciliation>(
            second.Reconciliation);
        Assert.Empty(secondReconciliation.AddedRoleIds);
        Assert.Empty(secondReconciliation.RemovedRoleIds);
    }

    [Fact]
    public async Task ExecuteAsync_AddCommitsThenThrows_TrustsObservedFinalState()
    {
        var expected = new InvalidOperationException("response was lost");
        var fake = new RecordingOperations(
            CreateSettings([SecondRoleId]),
            [UnconfiguredRoleId]);
        fake.AddRoleHandler = (roleId, _) =>
        {
            fake.MemberRoleIds.Add(roleId);
            return Task.FromException(expected);
        };

        var result = await ExecuteAsync(fake, ["11"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedComplete, result.Status);
        var synchronization = Assert.IsType<RoleMenuSynchronizationResult>(
            result.Synchronization);
        Assert.Same(expected, Assert.Single(synchronization.Failures).Exception);
        var reconciliation = Assert.IsType<RoleMenuSelectionReconciliation>(
            result.Reconciliation);
        Assert.Equal([SecondRoleId], reconciliation.AddedRoleIds);
        Assert.True(reconciliation.IsComplete);
    }

    [Fact]
    public async Task ExecuteAsync_PartialMutationFailure_ReturnsObservedIncompleteState()
    {
        var expected = new InvalidOperationException("one role failed");
        var fake = new RecordingOperations(
            CreateSettings([SecondRoleId, ThirdRoleId]),
            [UnconfiguredRoleId]);
        fake.AddRoleHandler = (roleId, _) =>
        {
            if (roleId == ThirdRoleId)
            {
                return Task.FromException(expected);
            }

            fake.MemberRoleIds.Add(roleId);
            return Task.CompletedTask;
        };

        var result = await ExecuteAsync(fake, ["11", "12"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedIncomplete, result.Status);
        Assert.Equal(["add:11", "add:12"], fake.MutationCalls);
        Assert.Same(
            expected,
            Assert.Single(
                Assert.IsType<RoleMenuSynchronizationResult>(result.Synchronization)
                    .Failures).Exception);
        var reconciliation = Assert.IsType<RoleMenuSelectionReconciliation>(
            result.Reconciliation);
        Assert.Equal([ThirdRoleId], reconciliation.MissingSelectedRoleIds);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMutation_UsesIndependentBoundedReadAndReconcilesCommit()
    {
        using var operationCancellation = new CancellationTokenSource();
        var fake = new RecordingOperations(
            CreateSettings([SecondRoleId]),
            [UnconfiguredRoleId]);
        fake.AddRoleHandler = (roleId, _) =>
        {
            fake.MemberRoleIds.Add(roleId);
            operationCancellation.Cancel();
            return Task.FromCanceled(operationCancellation.Token);
        };

        var result = await ExecuteAsync(
            fake,
            ["11"],
            cancellationToken: operationCancellation.Token);

        Assert.Equal(RoleMenuMemberWorkflowStatus.ConfirmedComplete, result.Status);
        var interruption = Assert.IsType<RoleMenuMutationInterruption>(
            Assert.IsType<RoleMenuSynchronizationResult>(result.Synchronization).Interruption);
        Assert.Equal(RoleMenuMutationInterruptionKind.OutcomeUnknown, interruption.Kind);
        Assert.Equal(2, fake.MemberReadTokens.Count);
        Assert.Equal(operationCancellation.Token, fake.MemberReadTokens[0]);
        Assert.True(fake.MemberReadTokens[0].IsCancellationRequested);
        Assert.NotEqual(operationCancellation.Token, fake.MemberReadTokens[1]);
        Assert.True(fake.MemberReadTokens[1].CanBeCanceled);
        Assert.False(fake.MemberReadTokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task ExecuteAsync_FinalReadFailure_IsExplicitlyUnconfirmed()
    {
        var expected = new InvalidOperationException("Discord unavailable");
        var fake = new RecordingOperations(
            CreateSettings([SecondRoleId]),
            [UnconfiguredRoleId]);
        fake.ReadMemberHandler = (readNumber, _) => readNumber == 1
            ? Task.FromResult<RoleMenuMemberSnapshot?>(fake.CreateCurrentMember())
            : Task.FromException<RoleMenuMemberSnapshot?>(expected);

        var result = await ExecuteAsync(fake, ["11"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.Unconfirmed, result.Status);
        Assert.False(result.IsConfirmed);
        Assert.False(result.IsComplete);
        Assert.Equal(RoleMenuMemberFinalStateIssue.ReadFailed, result.FinalStateIssue);
        Assert.Same(expected, result.FinalReadException);
        Assert.Null(result.Reconciliation);
        Assert.Equal(2, fake.MemberReadTokens.Count);
        Assert.True(fake.MemberReadTokens[1].CanBeCanceled);
    }

    [Theory]
    [InlineData(0, (int)RoleMenuMemberFinalStateIssue.MemberMissing)]
    [InlineData(1, (int)RoleMenuMemberFinalStateIssue.SnapshotMismatch)]
    public async Task ExecuteAsync_UnusableFinalMember_IsExplicitlyUnconfirmed(
        int finalMemberCase,
        int expectedIssue)
    {
        var fake = new RecordingOperations(
            CreateSettings([SecondRoleId]),
            [UnconfiguredRoleId]);
        fake.ReadMemberHandler = (readNumber, _) =>
        {
            if (readNumber == 1)
            {
                return Task.FromResult<RoleMenuMemberSnapshot?>(fake.CreateCurrentMember());
            }

            return Task.FromResult<RoleMenuMemberSnapshot?>(finalMemberCase == 0
                ? null
                : fake.CreateCurrentMember() with { UserId = MemberUserId + 1UL });
        };

        var result = await ExecuteAsync(fake, ["11"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.Unconfirmed, result.Status);
        Assert.Equal((RoleMenuMemberFinalStateIssue)expectedIssue, result.FinalStateIssue);
        Assert.Null(result.Reconciliation);
    }

    [Theory]
    [InlineData(
        0,
        (int)RoleMenuMemberConfigurationIssue.SettingsMissing,
        (int)RoleMenuSettingsIssue.None)]
    [InlineData(
        1,
        (int)RoleMenuMemberConfigurationIssue.SettingsIdentityMismatch,
        (int)RoleMenuSettingsIssue.None)]
    [InlineData(
        2,
        (int)RoleMenuMemberConfigurationIssue.SettingsInvalid,
        (int)RoleMenuSettingsIssue.InvalidRoleId)]
    [InlineData(
        3,
        (int)RoleMenuMemberConfigurationIssue.GuildMismatch,
        (int)RoleMenuSettingsIssue.None)]
    [InlineData(
        4,
        (int)RoleMenuMemberConfigurationIssue.BoundPanelMismatch,
        (int)RoleMenuSettingsIssue.None)]
    public async Task ExecuteAsync_RejectsMissingMalformedCrossGuildOrStaleSettings(
        int settingsCase,
        int expectedIssue,
        int expectedSettingsIssue)
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId, SecondRoleId]),
            [FirstRoleId]);
        var boundMessageId = MessageId;
        fake.Settings = settingsCase switch
        {
            0 => null,
            1 => CreateSettings([FirstRoleId], id: ObjectId.GenerateNewId()),
            2 => CreateRawSettings(roleIds: ["not-a-role"]),
            3 => CreateSettings([FirstRoleId], guildId: GuildId + 1UL),
            _ => fake.Settings
        };
        if (settingsCase == 4)
        {
            boundMessageId++;
        }

        var result = await ExecuteAsync(fake, ["10"], boundMessageId);

        Assert.Equal(RoleMenuMemberWorkflowStatus.InvalidConfiguration, result.Status);
        Assert.Equal(
            (RoleMenuMemberConfigurationIssue)expectedIssue,
            result.ConfigurationIssue);
        Assert.Equal((RoleMenuSettingsIssue)expectedSettingsIssue, result.SettingsIssue);
        Assert.Empty(fake.MutationCalls);
        Assert.Empty(fake.MemberReadTokens);
    }

    [Fact]
    public async Task ExecuteAsync_MissingPanel_StopsBeforeAuthorizationOrMemberReads()
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId]),
            [FirstRoleId])
        {
            Panel = null
        };

        var result = await ExecuteAsync(fake, ["10"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.InvalidConfiguration, result.Status);
        Assert.Equal(RoleMenuMemberConfigurationIssue.PanelMissing, result.ConfigurationIssue);
        Assert.Empty(fake.BotReadTokens);
        Assert.Empty(fake.MemberReadTokens);
        Assert.Empty(fake.MutationCalls);
    }

    [Fact]
    public async Task ExecuteAsync_InteractionFromDifferentChannel_IsRejectedAsStale()
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId]),
            [FirstRoleId]);

        var result = await ExecuteAsync(
            fake,
            ["10"],
            interactionChannelId: ChannelId + 1UL);

        Assert.Equal(RoleMenuMemberWorkflowStatus.InvalidConfiguration, result.Status);
        Assert.Equal(RoleMenuMemberConfigurationIssue.PanelInvalid, result.ConfigurationIssue);
        Assert.Equal(RoleMenuPanelContextIssue.ChannelMismatch, result.PanelIssue);
        Assert.Empty(fake.BotReadTokens);
        Assert.Empty(fake.MemberReadTokens);
        Assert.Empty(fake.MutationCalls);
    }

    [Theory]
    [InlineData(0, (int)RoleMenuPanelContextIssue.GuildMismatch)]
    [InlineData(1, (int)RoleMenuPanelContextIssue.ChannelMismatch)]
    [InlineData(2, (int)RoleMenuPanelContextIssue.MessageMismatch)]
    [InlineData(3, (int)RoleMenuPanelContextIssue.UnexpectedAuthor)]
    [InlineData(4, (int)RoleMenuPanelContextIssue.MissingManageButton)]
    public async Task ExecuteAsync_RejectsForeignOrNonCanonicalPanel(
        int panelCase,
        int expectedIssue)
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId]),
            [FirstRoleId]);
        var panel = Assert.IsType<RoleMenuPanelSnapshot>(fake.Panel);
        fake.Panel = panelCase switch
        {
            0 => panel with { GuildId = GuildId + 1UL },
            1 => panel with { ChannelId = ChannelId + 1UL },
            2 => panel with { MessageId = MessageId + 1UL },
            3 => panel with { AuthorId = BotUserId + 1UL },
            _ => panel with { HasManageButton = false }
        };

        var result = await ExecuteAsync(fake, ["10"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.InvalidConfiguration, result.Status);
        Assert.Equal(RoleMenuMemberConfigurationIssue.PanelInvalid, result.ConfigurationIssue);
        Assert.Equal((RoleMenuPanelContextIssue)expectedIssue, result.PanelIssue);
        Assert.Empty(fake.BotReadTokens);
        Assert.Empty(fake.MemberReadTokens);
        Assert.Empty(fake.MutationCalls);
    }

    [Theory]
    [InlineData(0, (int)RoleMenuRoleIssueKind.BotMissingManageRoles)]
    [InlineData(1, (int)RoleMenuRoleIssueKind.Missing)]
    [InlineData(2, (int)RoleMenuRoleIssueKind.Everyone)]
    [InlineData(3, (int)RoleMenuRoleIssueKind.Managed)]
    [InlineData(4, (int)RoleMenuRoleIssueKind.BotHierarchy)]
    public async Task ExecuteAsync_RejectsFreshBotPermissionOrRoleSafetyFailure(
        int roleCase,
        int expectedIssue)
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId]),
            [FirstRoleId]);
        var bot = Assert.IsType<RoleMenuBotSnapshot>(fake.Bot);
        fake.Bot = roleCase switch
        {
            0 => bot with { Actor = bot.Actor with { CanManageRoles = false } },
            1 => bot with { Roles = [] },
            2 => bot with { Roles = [CreateRole(FirstRoleId, isEveryone: true)] },
            3 => bot with { Roles = [CreateRole(FirstRoleId, isManaged: true)] },
            _ => bot with
            {
                Roles = [CreateRole(FirstRoleId, position: bot.Actor.Hierarchy)]
            }
        };

        var result = await ExecuteAsync(fake, ["10"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.InvalidConfiguration, result.Status);
        Assert.Equal(RoleMenuMemberConfigurationIssue.RolesInvalid, result.ConfigurationIssue);
        Assert.Equal(
            (RoleMenuRoleIssueKind)expectedIssue,
            Assert.Single(result.RoleIssues!).Kind);
        Assert.Empty(fake.MemberReadTokens);
        Assert.Empty(fake.MutationCalls);
    }

    [Theory]
    [InlineData(0, (int)RoleMenuMemberConfigurationIssue.BotMissing)]
    [InlineData(1, (int)RoleMenuMemberConfigurationIssue.BotSnapshotMismatch)]
    public async Task ExecuteAsync_RejectsMissingOrMismatchedFreshBot(
        int botCase,
        int expectedIssue)
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId]),
            [FirstRoleId]);
        fake.Bot = botCase == 0
            ? null
            : Assert.IsType<RoleMenuBotSnapshot>(fake.Bot) with
            {
                UserId = BotUserId + 1UL
            };

        var result = await ExecuteAsync(fake, ["10"]);

        Assert.Equal(RoleMenuMemberWorkflowStatus.InvalidConfiguration, result.Status);
        Assert.Equal(
            (RoleMenuMemberConfigurationIssue)expectedIssue,
            result.ConfigurationIssue);
        Assert.Empty(fake.MemberReadTokens);
        Assert.Empty(fake.MutationCalls);
    }

    [Theory]
    [InlineData(0, (int)RoleMenuMemberWorkflowStatus.MemberUnavailable)]
    [InlineData(1, (int)RoleMenuMemberWorkflowStatus.InvalidConfiguration)]
    public async Task ExecuteAsync_RejectsMissingOrMismatchedFreshMember(
        int memberCase,
        int expectedStatus)
    {
        var fake = new RecordingOperations(
            CreateSettings([FirstRoleId]),
            [FirstRoleId]);
        fake.ReadMemberHandler = (_, _) => Task.FromResult<RoleMenuMemberSnapshot?>(
            memberCase == 0
                ? null
                : fake.CreateCurrentMember() with { GuildId = GuildId + 1UL });

        var result = await ExecuteAsync(fake, ["10"]);

        Assert.Equal((RoleMenuMemberWorkflowStatus)expectedStatus, result.Status);
        if (memberCase == 1)
        {
            Assert.Equal(
                RoleMenuMemberConfigurationIssue.MemberSnapshotMismatch,
                result.ConfigurationIssue);
        }

        Assert.Single(fake.MemberReadTokens);
        Assert.Empty(fake.MutationCalls);
    }

    [Theory]
    [MemberData(nameof(InvalidSelections))]
    public async Task ExecuteAsync_RejectsTamperedOrInvalidSelectionWithoutMutation(
        string[] selectedRoleValues,
        int selectionMode,
        int expectedIssue)
    {
        var fake = new RecordingOperations(
            CreateSettings(
                [FirstRoleId, SecondRoleId],
                (RoleMenuSelectionMode)selectionMode),
            [FirstRoleId]);

        var result = await ExecuteAsync(fake, selectedRoleValues);

        Assert.Equal(RoleMenuMemberWorkflowStatus.InvalidSelection, result.Status);
        Assert.Equal((RoleMenuSelectionIssue)expectedIssue, result.SelectionIssue);
        Assert.Single(fake.MemberReadTokens);
        Assert.Empty(fake.MutationCalls);
        Assert.Null(result.Reconciliation);
    }

    private static Task<RoleMenuMemberWorkflowResult> ExecuteAsync(
        RecordingOperations fake,
        IReadOnlyCollection<string> selectedRoleValues,
        ulong boundMessageId = MessageId,
        ulong interactionChannelId = ChannelId,
        CancellationToken cancellationToken = default)
        => RoleMenuMemberWorkflow.ExecuteAsync(
            MenuId,
            GuildId,
            interactionChannelId,
            BotUserId,
            MemberUserId,
            boundMessageId,
            selectedRoleValues,
            fake.CreateOperations(),
            cancellationToken);

    private static RoleMenuSettings CreateSettings(
        IReadOnlyCollection<ulong> roleIds,
        RoleMenuSelectionMode selectionMode = RoleMenuSelectionMode.Multiple,
        ObjectId? id = null,
        ulong guildId = GuildId)
        => new(
            id ?? MenuId,
            guildId.ToString(CultureInfo.InvariantCulture),
            ChannelId.ToString(CultureInfo.InvariantCulture),
            MessageId.ToString(CultureInfo.InvariantCulture),
            "Games",
            "Choose games",
            roleIds.Select(roleId => roleId.ToString(CultureInfo.InvariantCulture)),
            selectionMode);

    private static RoleMenuSettings CreateRawSettings(
        IReadOnlyCollection<string> roleIds)
        => new(
            MenuId,
            GuildId.ToString(CultureInfo.InvariantCulture),
            ChannelId.ToString(CultureInfo.InvariantCulture),
            MessageId.ToString(CultureInfo.InvariantCulture),
            "Games",
            "Choose games",
            roleIds,
            RoleMenuSelectionMode.Multiple);

    private static RoleMenuRoleSnapshot CreateRole(
        ulong roleId,
        bool isEveryone = false,
        bool isManaged = false,
        int position = 10)
        => new(roleId, $"Role {roleId}", isEveryone, isManaged, position);

    private sealed class RecordingOperations
    {
        private int _memberReadCount;

        internal RecordingOperations(
            RoleMenuSettings settings,
            IEnumerable<ulong> memberRoleIds)
        {
            Settings = settings;
            Panel = new RoleMenuPanelSnapshot(
                GuildId,
                ChannelId,
                MessageId,
                BotUserId,
                true);
            Bot = new RoleMenuBotSnapshot(
                GuildId,
                BotUserId,
                [
                    CreateRole(FirstRoleId),
                    CreateRole(SecondRoleId),
                    CreateRole(ThirdRoleId)
                ],
                new RoleMenuActorSnapshot(true, 100, false));
            MemberRoleIds = memberRoleIds.ToHashSet();
        }

        internal RoleMenuSettings? Settings { get; set; }
        internal RoleMenuPanelSnapshot? Panel { get; set; }
        internal RoleMenuBotSnapshot? Bot { get; set; }
        internal HashSet<ulong> MemberRoleIds { get; }
        internal List<string> MutationCalls { get; } = [];
        internal List<CancellationToken> BotReadTokens { get; } = [];
        internal List<CancellationToken> MemberReadTokens { get; } = [];
        internal List<ObjectId> PanelReadMenuIds { get; } = [];
        internal List<(ulong ChannelId, ulong MessageId)> PanelReadLocations { get; } = [];
        internal Func<int, CancellationToken, Task<RoleMenuMemberSnapshot?>>?
            ReadMemberHandler { get; set; }
        internal Func<ulong, CancellationToken, Task>? AddRoleHandler { get; set; }
        internal Func<ulong, CancellationToken, Task>? RemoveRoleHandler { get; set; }

        internal RoleMenuMemberOperations CreateOperations()
            => new(
                ReadSettingsAsync,
                ReadPanelAsync,
                ReadBotAsync,
                ReadMemberAsync,
                AddRoleAsync,
                RemoveRoleAsync);

        internal RoleMenuMemberSnapshot CreateCurrentMember()
            => new(GuildId, MemberUserId, MemberRoleIds.Order().ToList());

        private Task<RoleMenuSettings?> ReadSettingsAsync(
            ObjectId menuId,
            ulong guildId,
            CancellationToken cancellationToken)
        {
            _ = menuId;
            _ = guildId;
            _ = cancellationToken;
            return Task.FromResult(Settings);
        }

        private Task<RoleMenuPanelSnapshot?> ReadPanelAsync(
            ObjectId menuId,
            ulong channelId,
            ulong messageId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            PanelReadMenuIds.Add(menuId);
            PanelReadLocations.Add((channelId, messageId));
            return Task.FromResult(Panel);
        }

        private Task<RoleMenuBotSnapshot?> ReadBotAsync(
            ulong guildId,
            ulong userId,
            CancellationToken cancellationToken)
        {
            _ = guildId;
            _ = userId;
            BotReadTokens.Add(cancellationToken);
            return Task.FromResult(Bot);
        }

        private Task<RoleMenuMemberSnapshot?> ReadMemberAsync(
            ulong guildId,
            ulong userId,
            CancellationToken cancellationToken)
        {
            _ = guildId;
            _ = userId;
            _memberReadCount++;
            MemberReadTokens.Add(cancellationToken);
            return ReadMemberHandler is null
                ? Task.FromResult<RoleMenuMemberSnapshot?>(CreateCurrentMember())
                : ReadMemberHandler(_memberReadCount, cancellationToken);
        }

        private Task AddRoleAsync(
            ulong guildId,
            ulong userId,
            ulong roleId,
            CancellationToken cancellationToken)
        {
            _ = guildId;
            _ = userId;
            MutationCalls.Add($"add:{roleId}");
            if (AddRoleHandler is not null)
            {
                return AddRoleHandler(roleId, cancellationToken);
            }

            MemberRoleIds.Add(roleId);
            return Task.CompletedTask;
        }

        private Task RemoveRoleAsync(
            ulong guildId,
            ulong userId,
            ulong roleId,
            CancellationToken cancellationToken)
        {
            _ = guildId;
            _ = userId;
            MutationCalls.Add($"remove:{roleId}");
            if (RemoveRoleHandler is not null)
            {
                return RemoveRoleHandler(roleId, cancellationToken);
            }

            MemberRoleIds.Remove(roleId);
            return Task.CompletedTask;
        }
    }
}
