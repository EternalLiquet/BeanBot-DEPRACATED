using System.Globalization;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuDeletionWorkflowTests
{
    private const ulong GuildId = 101;
    private const ulong BotUserId = 202;
    private const ulong ChannelId = 303;
    private const ulong MessageId = 404;
    private static readonly ObjectId MenuId = ObjectId.Parse("64e7611aaac75f172f0f1234");

    [Fact]
    public async Task ExecuteAsync_DeniedFreshAuthorization_PerformsNoReadsOrWrites()
    {
        var fake = new RecordingOperations(CreateSettings());

        var result = await ExecuteAsync(fake, administratorCanManageRoles: false);

        Assert.True(result.AuthorizationDenied);
        Assert.Equal(RoleMenuConfigurationDeletionStatus.Kept, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.DeletedOrMissing, result.PanelStatus);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_MissingConfiguration_IsAlreadyDeleted()
    {
        var fake = new RecordingOperations(settings: null);

        var result = await ExecuteAsync(fake);

        Assert.Equal(
            RoleMenuConfigurationDeletionStatus.AlreadyMissing,
            result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.DeletedOrMissing, result.PanelStatus);
        Assert.Equal(["read-settings"], fake.Calls.Select(call => call.Name));
    }

    [Fact]
    public async Task ExecuteAsync_UsesOnlyPanelLocationFromPotentiallyCorruptSettings()
    {
        var settings = new RoleMenuSettings(
            MenuId,
            "not-a-guild-id",
            ChannelId.ToString(CultureInfo.InvariantCulture),
            MessageId.ToString(CultureInfo.InvariantCulture),
            new string('x', 5000),
            "corrupt fields do not prevent safe deletion",
            ["not-a-role-id"],
            RoleMenuSelectionMode.Multiple);
        var fake = new RecordingOperations(settings);

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Deleted, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.DeletedOrMissing, result.PanelStatus);
        Assert.Equal(
            ["read-settings", "read-panel", "delete-panel", "delete-settings"],
            fake.Calls.Select(call => call.Name));
        Assert.Equal([fake.Panel], fake.DeletedPanels);
    }

    [Theory]
    [InlineData("invalid", "404")]
    [InlineData("303", "invalid")]
    [InlineData("0", "404")]
    public async Task ExecuteAsync_InvalidPanelLocation_DeletesOnlyConfiguration(
        string channelId,
        string messageId)
    {
        var fake = new RecordingOperations(CreateSettings(channelId, messageId));

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Deleted, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.UnexpectedMessage, result.PanelStatus);
        Assert.Equal(RoleMenuPanelDeletionIssue.InvalidLocation, result.PanelIssue);
        Assert.Equal(["read-settings", "delete-settings"], fake.Calls.Select(call => call.Name));
        Assert.Empty(fake.DeletedPanels);
    }

    [Theory]
    [InlineData(
        (int)RoleMenuPanelLookupStatus.ChannelMissing,
        (int)RoleMenuPanelDeletionStatus.DeletedOrMissing,
        (int)RoleMenuPanelDeletionIssue.ChannelMissing)]
    [InlineData(
        (int)RoleMenuPanelLookupStatus.MessageMissing,
        (int)RoleMenuPanelDeletionStatus.DeletedOrMissing,
        (int)RoleMenuPanelDeletionIssue.MessageMissing)]
    [InlineData(
        (int)RoleMenuPanelLookupStatus.UnexpectedChannelType,
        (int)RoleMenuPanelDeletionStatus.UnexpectedMessage,
        (int)RoleMenuPanelDeletionIssue.UnexpectedChannelType)]
    public async Task ExecuteAsync_NonDeletableLookup_RemovesConfigurationWithoutDeletingMessage(
        int lookupStatusValue,
        int expectedStatusValue,
        int expectedIssueValue)
    {
        var lookupStatus = (RoleMenuPanelLookupStatus)lookupStatusValue;
        var expectedStatus = (RoleMenuPanelDeletionStatus)expectedStatusValue;
        var expectedIssue = (RoleMenuPanelDeletionIssue)expectedIssueValue;
        var fake = new RecordingOperations(CreateSettings())
        {
            PanelLookup = new RoleMenuPanelLookupResult(lookupStatus)
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Deleted, result.ConfigurationStatus);
        Assert.Equal(expectedStatus, result.PanelStatus);
        Assert.Equal(expectedIssue, result.PanelIssue);
        Assert.Equal(
            ["read-settings", "read-panel", "delete-settings"],
            fake.Calls.Select(call => call.Name));
        Assert.Empty(fake.DeletedPanels);
    }

    [Theory]
    [InlineData((int)RoleMenuPanelDeletionIssue.GuildMismatch)]
    [InlineData((int)RoleMenuPanelDeletionIssue.ChannelMismatch)]
    [InlineData((int)RoleMenuPanelDeletionIssue.MessageMismatch)]
    [InlineData((int)RoleMenuPanelDeletionIssue.UnexpectedAuthor)]
    [InlineData((int)RoleMenuPanelDeletionIssue.MissingManageButton)]
    [InlineData((int)RoleMenuPanelDeletionIssue.InvalidLookup)]
    public async Task ExecuteAsync_NeverDeletesUnexpectedOrForeignPanel(
        int issueValue)
    {
        var issue = (RoleMenuPanelDeletionIssue)issueValue;
        var fake = new RecordingOperations(CreateSettings())
        {
            PanelLookup = CreateInvalidLookup(issue)
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Deleted, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.UnexpectedMessage, result.PanelStatus);
        Assert.Equal(issue, result.PanelIssue);
        Assert.DoesNotContain(fake.Calls, call => call.Name == "delete-panel");
        Assert.Empty(fake.DeletedPanels);
    }

    [Fact]
    public async Task ExecuteAsync_KnownPanelDeletionFailure_RetainsConfiguration()
    {
        var fake = new RecordingOperations(CreateSettings())
        {
            DeletePanelResult = false
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Kept, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.Failed, result.PanelStatus);
        Assert.Equal(RoleMenuPanelDeletionIssue.DeletionFailed, result.PanelIssue);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(fake.Calls, call => call.Name == "delete-settings");
    }

    [Fact]
    public async Task ExecuteAsync_PanelDeleteThrowsAndPanelRemains_RetainsConfiguration()
    {
        var expected = new InvalidOperationException("delete failed");
        var fake = new RecordingOperations(CreateSettings())
        {
            DeletePanelHandler = (_, _) => Task.FromException<bool>(expected)
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Kept, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.Failed, result.PanelStatus);
        Assert.Equal(RoleMenuPanelDeletionIssue.DeletionFailed, result.PanelIssue);
        AssertFailure(result, RoleMenuDeletionFailurePhase.PanelDeletion, expected);
        Assert.DoesNotContain(fake.Calls, call => call.Name == "delete-settings");
        AssertReconciliationToken(fake, "read-panel");
    }

    [Fact]
    public async Task ExecuteAsync_PanelDeleteCommitsThenThrows_ReconcilesAndDeletesConfiguration()
    {
        var expected = new InvalidOperationException("response was lost");
        var fake = new RecordingOperations(CreateSettings())
        {
            DeletePanelHandler = (_, _) => Task.FromException<bool>(expected),
            ReadPanelHandler = (call, _) => Task.FromResult(
                call == 1
                    ? FoundPanel()
                    : new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing))
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Deleted, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.DeletedOrMissing, result.PanelStatus);
        Assert.Equal(RoleMenuPanelDeletionIssue.MessageMissing, result.PanelIssue);
        AssertFailure(result, RoleMenuDeletionFailurePhase.PanelDeletion, expected);
        Assert.Equal(
            [
                "read-settings",
                "read-panel",
                "delete-panel",
                "read-panel",
                "delete-settings"
            ],
            fake.Calls.Select(call => call.Name));
        AssertReconciliationToken(fake, "read-panel");
    }

    [Fact]
    public async Task ExecuteAsync_InitialPanelReadThrowsAndReconciliationFindsForeignPanel_NeverDeletesIt()
    {
        var expected = new InvalidOperationException("initial read failed");
        var fake = new RecordingOperations(CreateSettings())
        {
            ReadPanelHandler = (call, _) => call == 1
                ? Task.FromException<RoleMenuPanelLookupResult>(expected)
                : Task.FromResult(CreateInvalidLookup(RoleMenuPanelDeletionIssue.UnexpectedAuthor))
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Deleted, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.UnexpectedMessage, result.PanelStatus);
        Assert.Equal(RoleMenuPanelDeletionIssue.UnexpectedAuthor, result.PanelIssue);
        AssertFailure(result, RoleMenuDeletionFailurePhase.PanelLookup, expected);
        Assert.Empty(fake.DeletedPanels);
        Assert.DoesNotContain(fake.Calls, call => call.Name == "delete-panel");
    }

    [Fact]
    public async Task ExecuteAsync_PanelReconciliationFails_RetainsConfigurationAsOutcomeUnknown()
    {
        var deletionFailure = new InvalidOperationException("delete failed");
        var reconciliationFailure = new InvalidOperationException("reconciliation failed");
        var fake = new RecordingOperations(CreateSettings())
        {
            DeletePanelHandler = (_, _) => Task.FromException<bool>(deletionFailure),
            ReadPanelHandler = (call, _) => call == 1
                ? Task.FromResult(FoundPanel())
                : Task.FromException<RoleMenuPanelLookupResult>(
                    reconciliationFailure)
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Kept, result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.OutcomeUnknown, result.PanelStatus);
        Assert.Equal(RoleMenuPanelDeletionIssue.ReconciliationFailed, result.PanelIssue);
        Assert.Collection(
            result.Failures,
            failure => AssertFailure(
                failure,
                RoleMenuDeletionFailurePhase.PanelDeletion,
                deletionFailure),
            failure => AssertFailure(
                failure,
                RoleMenuDeletionFailurePhase.PanelReconciliation,
                reconciliationFailure));
        Assert.DoesNotContain(fake.Calls, call => call.Name == "delete-settings");
    }

    [Fact]
    public async Task ExecuteAsync_ConfigurationDeleteCommitsThenThrows_ReconcilesAsMissing()
    {
        var expected = new InvalidOperationException("response was lost");
        var fake = new RecordingOperations(CreateSettings())
        {
            DeleteSettingsHandler = (operations, _) =>
            {
                operations.Settings = null;
                return Task.FromException<bool>(expected);
            }
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(
            RoleMenuConfigurationDeletionStatus.AlreadyMissing,
            result.ConfigurationStatus);
        Assert.Equal(RoleMenuPanelDeletionStatus.DeletedOrMissing, result.PanelStatus);
        AssertFailure(result, RoleMenuDeletionFailurePhase.PersistenceDeletion, expected);
        Assert.Equal(2, fake.Calls.Count(call => call.Name == "read-settings"));
        AssertReconciliationToken(fake, "read-settings");
    }

    [Fact]
    public async Task ExecuteAsync_ConfigurationDeleteFailsAndRecordRemains_ReportsKept()
    {
        var expected = new InvalidOperationException("database unavailable");
        var fake = new RecordingOperations(CreateSettings())
        {
            DeleteSettingsHandler = (_, _) => Task.FromException<bool>(expected)
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(RoleMenuConfigurationDeletionStatus.Kept, result.ConfigurationStatus);
        AssertFailure(result, RoleMenuDeletionFailurePhase.PersistenceDeletion, expected);
        Assert.Equal(2, fake.Calls.Count(call => call.Name == "read-settings"));
    }

    [Fact]
    public async Task ExecuteAsync_ConfigurationReconciliationFails_ReportsOutcomeUnknown()
    {
        var expected = new InvalidOperationException("delete failed");
        var reconciliationFailure = new InvalidOperationException("reconciliation failed");
        var fake = new RecordingOperations(CreateSettings())
        {
            DeleteSettingsHandler = (_, _) => Task.FromException<bool>(expected),
            ReadSettingsHandler = (call, _) => call == 1
                ? Task.FromResult<RoleMenuSettings?>(CreateSettings())
                : Task.FromException<RoleMenuSettings?>(
                    reconciliationFailure)
        };

        var result = await ExecuteAsync(fake);

        Assert.Equal(
            RoleMenuConfigurationDeletionStatus.OutcomeUnknown,
            result.ConfigurationStatus);
        Assert.Collection(
            result.Failures,
            failure => AssertFailure(
                failure,
                RoleMenuDeletionFailurePhase.PersistenceDeletion,
                expected),
            failure => AssertFailure(
                failure,
                RoleMenuDeletionFailurePhase.PersistenceReconciliation,
                reconciliationFailure));
    }

    [Fact]
    public async Task ExecuteAsync_EachReconciliationPhaseGetsFreshBoundedToken()
    {
        var fake = new RecordingOperations(CreateSettings())
        {
            DeletePanelHandler = (_, _) => Task.FromException<bool>(
                new InvalidOperationException("panel response lost")),
            ReadPanelHandler = (call, _) => Task.FromResult(
                call == 1
                    ? FoundPanel()
                    : new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing)),
            DeleteSettingsHandler = (operations, _) =>
            {
                operations.Settings = null;
                return Task.FromException<bool>(
                    new InvalidOperationException("database response lost"));
            }
        };
        using var operationCancellation = new CancellationTokenSource();

        var result = await ExecuteAsync(
            fake,
            cancellationToken: operationCancellation.Token);

        var panelTokens = fake.Calls
            .Where(call => call.Name == "read-panel")
            .Select(call => call.Token)
            .ToList();
        var settingsTokens = fake.Calls
            .Where(call => call.Name == "read-settings")
            .Select(call => call.Token)
            .ToList();
        Assert.Equal(2, panelTokens.Count);
        Assert.Equal(2, settingsTokens.Count);
        Assert.Equal(operationCancellation.Token, panelTokens[0]);
        Assert.Equal(operationCancellation.Token, settingsTokens[0]);
        Assert.True(panelTokens[1].CanBeCanceled);
        Assert.True(settingsTokens[1].CanBeCanceled);
        Assert.NotEqual(operationCancellation.Token, panelTokens[1]);
        Assert.NotEqual(operationCancellation.Token, settingsTokens[1]);
        Assert.NotEqual(panelTokens[1], settingsTokens[1]);
        Assert.Collection(
            result.Failures,
            failure => Assert.Equal(
                RoleMenuDeletionFailurePhase.PanelDeletion,
                failure.Phase),
            failure => Assert.Equal(
                RoleMenuDeletionFailurePhase.PersistenceDeletion,
                failure.Phase));
    }

    [Fact]
    public async Task ExecuteAsync_ShutdownCancellationDuringPanelDelete_Propagates()
    {
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        var fake = new RecordingOperations(CreateSettings())
        {
            ShuttingDown = true,
            DeletePanelHandler = (_, _) => Task.FromCanceled<bool>(shutdown.Token)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(fake));

        Assert.Equal(
            ["read-settings", "read-panel", "delete-panel"],
            fake.Calls.Select(call => call.Name));
    }

    [Fact]
    public async Task ExecuteAsync_ShutdownCancellationDuringConfigurationDelete_Propagates()
    {
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        var fake = new RecordingOperations(CreateSettings())
        {
            ShuttingDown = true,
            DeleteSettingsHandler = (_, _) => Task.FromCanceled<bool>(shutdown.Token)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(fake));

        Assert.Equal(1, fake.Calls.Count(call => call.Name == "read-settings"));
    }

    [Fact]
    public async Task ExecuteAsync_ShutdownCancellationDuringReconciliation_Propagates()
    {
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        var fake = new RecordingOperations(CreateSettings())
        {
            ShuttingDown = true,
            DeletePanelHandler = (_, _) => Task.FromException<bool>(
                new InvalidOperationException("delete failed")),
            ReadPanelHandler = (call, _) => call == 1
                ? Task.FromResult(FoundPanel())
                : Task.FromCanceled<RoleMenuPanelLookupResult>(shutdown.Token)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(fake));

        Assert.DoesNotContain(fake.Calls, call => call.Name == "delete-settings");
    }

    private static Task<RoleMenuDeletionResult> ExecuteAsync(
        RecordingOperations fake,
        bool administratorCanManageRoles = true,
        CancellationToken cancellationToken = default)
        => RoleMenuDeletionWorkflow.ExecuteAsync(
            MenuId,
            GuildId,
            BotUserId,
            administratorCanManageRoles,
            fake.Create(),
            cancellationToken);

    private static RoleMenuSettings CreateSettings(
        string? channelId = null,
        string? messageId = null)
        => new(
            MenuId,
            GuildId.ToString(CultureInfo.InvariantCulture),
            channelId ?? ChannelId.ToString(CultureInfo.InvariantCulture),
            messageId ?? MessageId.ToString(CultureInfo.InvariantCulture),
            "Choose roles",
            string.Empty,
            ["505"],
            RoleMenuSelectionMode.Multiple);

    private static RoleMenuPanelLookupResult FoundPanel()
        => new(
            RoleMenuPanelLookupStatus.Found,
            new RoleMenuPanelSnapshot(
                GuildId,
                ChannelId,
                MessageId,
                BotUserId,
                HasManageButton: true));

    private static RoleMenuPanelLookupResult CreateInvalidLookup(
        RoleMenuPanelDeletionIssue issue)
    {
        if (issue == RoleMenuPanelDeletionIssue.InvalidLookup)
        {
            return new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.Found);
        }

        var panel = issue switch
        {
            RoleMenuPanelDeletionIssue.GuildMismatch =>
                new RoleMenuPanelSnapshot(999, ChannelId, MessageId, BotUserId, true),
            RoleMenuPanelDeletionIssue.ChannelMismatch =>
                new RoleMenuPanelSnapshot(GuildId, 999, MessageId, BotUserId, true),
            RoleMenuPanelDeletionIssue.MessageMismatch =>
                new RoleMenuPanelSnapshot(GuildId, ChannelId, 999, BotUserId, true),
            RoleMenuPanelDeletionIssue.UnexpectedAuthor =>
                new RoleMenuPanelSnapshot(GuildId, ChannelId, MessageId, 999, true),
            RoleMenuPanelDeletionIssue.MissingManageButton =>
                new RoleMenuPanelSnapshot(GuildId, ChannelId, MessageId, BotUserId, false),
            _ => throw new ArgumentOutOfRangeException(nameof(issue), issue, null)
        };
        return new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.Found, panel);
    }

    private static void AssertFailure(
        RoleMenuDeletionResult result,
        RoleMenuDeletionFailurePhase phase,
        Exception exception)
        => AssertFailure(Assert.Single(result.Failures), phase, exception);

    private static void AssertFailure(
        RoleMenuDeletionFailure failure,
        RoleMenuDeletionFailurePhase phase,
        Exception exception)
    {
        Assert.Equal(phase, failure.Phase);
        Assert.Same(exception, failure.Exception);
    }

    private static void AssertReconciliationToken(
        RecordingOperations fake,
        string operationName)
    {
        var calls = fake.Calls.Where(call => call.Name == operationName).ToList();
        Assert.Equal(2, calls.Count);
        Assert.True(calls[1].Token.CanBeCanceled);
        Assert.NotEqual(calls[0].Token, calls[1].Token);
    }

    private sealed record OperationCall(string Name, CancellationToken Token);

    private sealed class RecordingOperations
    {
        private int _readPanelCalls;
        private int _readSettingsCalls;

        internal RecordingOperations(RoleMenuSettings? settings)
        {
            Settings = settings;
        }

        internal List<OperationCall> Calls { get; } = [];
        internal List<RoleMenuPanelSnapshot> DeletedPanels { get; } = [];
        internal RoleMenuSettings? Settings { get; set; }
        internal RoleMenuPanelSnapshot Panel { get; } = FoundPanel().Panel!;
        internal RoleMenuPanelLookupResult PanelLookup { get; init; } = FoundPanel();
        internal bool DeletePanelResult { get; init; } = true;
        internal bool DeleteSettingsResult { get; init; } = true;
        internal bool ShuttingDown { get; init; }
        internal Func<int, CancellationToken, Task<RoleMenuSettings?>>? ReadSettingsHandler
        {
            get;
            init;
        }

        internal Func<int, CancellationToken, Task<RoleMenuPanelLookupResult>>? ReadPanelHandler
        {
            get;
            init;
        }

        internal Func<RecordingOperations, CancellationToken, Task<bool>>? DeletePanelHandler
        {
            get;
            init;
        }

        internal Func<RecordingOperations, CancellationToken, Task<bool>>? DeleteSettingsHandler
        {
            get;
            init;
        }

        internal RoleMenuDeletionOperations Create()
            => new(
                ReadSettingsAsync,
                ReadPanelAsync,
                DeletePanelAsync,
                DeleteSettingsAsync,
                () => ShuttingDown);

        private Task<RoleMenuSettings?> ReadSettingsAsync(
            ObjectId menuId,
            ulong guildId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(MenuId, menuId);
            Assert.Equal(GuildId, guildId);
            Calls.Add(new OperationCall("read-settings", cancellationToken));
            var call = ++_readSettingsCalls;
            return ReadSettingsHandler is null
                ? Task.FromResult(Settings)
                : ReadSettingsHandler(call, cancellationToken);
        }

        private Task<RoleMenuPanelLookupResult> ReadPanelAsync(
            ObjectId menuId,
            ulong channelId,
            ulong messageId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(MenuId, menuId);
            Assert.Equal(ChannelId, channelId);
            Assert.Equal(MessageId, messageId);
            Calls.Add(new OperationCall("read-panel", cancellationToken));
            var call = ++_readPanelCalls;
            return ReadPanelHandler is null
                ? Task.FromResult(PanelLookup)
                : ReadPanelHandler(call, cancellationToken);
        }

        private Task<bool> DeletePanelAsync(
            RoleMenuPanelSnapshot panel,
            CancellationToken cancellationToken)
        {
            Calls.Add(new OperationCall("delete-panel", cancellationToken));
            DeletedPanels.Add(panel);
            return DeletePanelHandler is null
                ? Task.FromResult(DeletePanelResult)
                : DeletePanelHandler(this, cancellationToken);
        }

        private Task<bool> DeleteSettingsAsync(
            ObjectId menuId,
            ulong guildId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(MenuId, menuId);
            Assert.Equal(GuildId, guildId);
            Calls.Add(new OperationCall("delete-settings", cancellationToken));
            return DeleteSettingsHandler is null
                ? Task.FromResult(DeleteSettingsResult)
                : DeleteSettingsHandler(this, cancellationToken);
        }
    }
}
