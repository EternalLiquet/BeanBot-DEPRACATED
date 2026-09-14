using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class LegacyReactionRoleRetirementWorkflowTests
{
    private const ulong GuildId = 1;
    private const ulong ChannelId = 2;
    private const ulong MessageId = 3;

    [Fact]
    public async Task ExecuteAsync_DeletesPanelBeforeExactSettings()
    {
        var order = new List<string>();
        var settings = CreateSettings();
        var operations = CreateOperations(
            readSettings: (_, _) => Task.FromResult<ReactionRoleSettings?>(settings),
            readPanel: (source, _) =>
            {
                order.Add("read-panel");
                return Task.FromResult(Found(source));
            },
            deletePanel: (_, _, _) =>
            {
                order.Add("delete-panel");
                return Task.FromResult(true);
            },
            deleteSettings: (_, _, _) =>
            {
                order.Add("delete-settings");
                return Task.FromResult(true);
            });

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.Retired, result.Status);
        Assert.False(result.SourceWasMissing);
        Assert.Equal(["read-panel", "delete-panel", "delete-settings"], order);
    }

    [Fact]
    public async Task ExecuteAsync_MissingPanelSkipsDiscordDeleteAndCleansStaleSettings()
    {
        var deletePanelCalls = 0;
        var deleteSettingsCalls = 0;
        var operations = CreateOperations(
            readSettings: (_, _) => Task.FromResult<ReactionRoleSettings?>(CreateSettings()),
            readPanel: (_, _) => Task.FromResult(
                new LegacyReactionRolePanelLookupResult(
                    LegacyReactionRolePanelLookupStatus.MessageMissing)),
            deletePanel: (_, _, _) =>
            {
                deletePanelCalls++;
                return Task.FromResult(true);
            },
            deleteSettings: (_, _, _) =>
            {
                deleteSettingsCalls++;
                return Task.FromResult(true);
            });

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.Retired, result.Status);
        Assert.True(result.SourceWasMissing);
        Assert.Equal(0, deletePanelCalls);
        Assert.Equal(1, deleteSettingsCalls);
    }

    [Fact]
    public Task ExecuteAsync_UnrecognizedSourceNeverDeletesAnything()
        => AssertUnsafeSourceNeverDeletesAnythingAsync(
            LegacyReactionRolePanelLookupStatus.Unrecognized);

    [Fact]
    public Task ExecuteAsync_UnexpectedChannelNeverDeletesAnything()
        => AssertUnsafeSourceNeverDeletesAnythingAsync(
            LegacyReactionRolePanelLookupStatus.UnexpectedChannel);

    [Fact]
    public async Task ExecuteAsync_PermissionLossAtConfirmationStopsBeforeSourceRead()
    {
        var settingsReads = 0;
        var operations = CreateOperations(
            canManageRoles: _ => Task.FromResult<bool?>(false),
            readSettings: (_, _) =>
            {
                settingsReads++;
                return Task.FromResult<ReactionRoleSettings?>(CreateSettings());
            });

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.AuthorizationDenied, result.Status);
        Assert.Equal(0, settingsReads);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyMissingSettingsIsIdempotent()
    {
        var panelReads = 0;
        var operations = CreateOperations(
            readSettings: (_, _) => Task.FromResult<ReactionRoleSettings?>(null),
            readPanel: (_, _) =>
            {
                panelReads++;
                return Task.FromResult(new LegacyReactionRolePanelLookupResult(
                    LegacyReactionRolePanelLookupStatus.MessageMissing));
            });

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.AlreadyRetired, result.Status);
        Assert.Equal(0, panelReads);
    }

    [Fact]
    public async Task ExecuteAsync_CrossGuildSavedRecordNeverTouchesDiscord()
    {
        var panelReads = 0;
        var operations = CreateOperations(
            readSettings: (_, _) => Task.FromResult<ReactionRoleSettings?>(
                CreateSettings(guildId: 99)),
            readPanel: (_, _) =>
            {
                panelReads++;
                return Task.FromResult(new LegacyReactionRolePanelLookupResult(
                    LegacyReactionRolePanelLookupStatus.MessageMissing));
            });

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(
            LegacyReactionRoleRetirementStatus.InvalidSavedConfiguration,
            result.Status);
        Assert.Equal(0, panelReads);
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousDiscordDelete_ReconcilesByReadButKeepsPersistence()
    {
        var panelReads = 0;
        var panelDeletes = 0;
        var settingsDeletes = 0;
        var source = Parse(CreateSettings());
        var operations = CreateOperations(
            readSettings: (_, _) => Task.FromResult<ReactionRoleSettings?>(CreateSettings()),
            readPanel: (_, _) =>
            {
                panelReads++;
                return Task.FromResult(panelReads == 1
                    ? Found(source)
                    : new LegacyReactionRolePanelLookupResult(
                        LegacyReactionRolePanelLookupStatus.MessageMissing));
            },
            deletePanel: (_, _, _) =>
            {
                panelDeletes++;
                throw new OperationCanceledException("Discord request timed out after dispatch.");
            },
            deleteSettings: (_, _, _) =>
            {
                settingsDeletes++;
                return Task.FromResult(true);
            });

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.PanelOutcomeUnknown, result.Status);
        Assert.True(result.SourceWasMissing);
        Assert.Equal(2, panelReads);
        Assert.Equal(1, panelDeletes);
        Assert.Equal(0, settingsDeletes);
        Assert.IsType<OperationCanceledException>(result.Failure);
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousDiscordDeleteStillPresent_KeepsSettings()
    {
        var panelReads = 0;
        var panelDeletes = 0;
        var settingsDeletes = 0;
        var source = Parse(CreateSettings());
        var operations = CreateOperations(
            readSettings: (_, _) => Task.FromResult<ReactionRoleSettings?>(CreateSettings()),
            readPanel: (_, _) =>
            {
                panelReads++;
                return Task.FromResult(Found(source));
            },
            deletePanel: (_, _, _) =>
            {
                panelDeletes++;
                throw new TimeoutException("Discord outcome unknown.");
            },
            deleteSettings: (_, _, _) =>
            {
                settingsDeletes++;
                return Task.FromResult(true);
            });

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(
            LegacyReactionRoleRetirementStatus.PanelDeletionFailed,
            result.Status);
        Assert.Equal(2, panelReads);
        Assert.Equal(1, panelDeletes);
        Assert.Equal(0, settingsDeletes);
    }

    [Fact]
    public async Task ExecuteAsync_PersistenceFailureAfterPanelDelete_RerunCanFinishCleanup()
    {
        var settingsReads = 0;
        var deletePanelCalls = 0;
        var operations = CreateOperations(
            readSettings: (_, _) =>
            {
                settingsReads++;
                return Task.FromResult<ReactionRoleSettings?>(CreateSettings());
            },
            deletePanel: (_, _, _) =>
            {
                deletePanelCalls++;
                return Task.FromResult(true);
            },
            deleteSettings: (_, _, _) =>
                throw new InvalidOperationException("Mongo unavailable"));

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.PersistenceKept, result.Status);
        Assert.Equal(2, settingsReads);
        Assert.Equal(1, deletePanelCalls);
        Assert.IsType<InvalidOperationException>(result.Failure);
    }

    [Fact]
    public async Task ExecuteAsync_PersistenceDeleteReturnedFalseAndRecordRemains_ReportsPartialRetirement()
    {
        var settingsReads = 0;
        var operations = CreateOperations(
            readSettings: (_, _) =>
            {
                settingsReads++;
                return Task.FromResult<ReactionRoleSettings?>(CreateSettings());
            },
            deleteSettings: (_, _, _) => Task.FromResult(false));

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.PersistenceKept, result.Status);
        Assert.Equal(2, settingsReads);
    }

    [Fact]
    public async Task ExecuteAsync_PersistenceDeleteReturnedFalseButRecordIsGone_ReconcilesSuccess()
    {
        var settingsReads = 0;
        var operations = CreateOperations(
            readSettings: (_, _) =>
            {
                settingsReads++;
                return Task.FromResult<ReactionRoleSettings?>(
                    settingsReads == 1 ? CreateSettings() : null);
            },
            deleteSettings: (_, _, _) => Task.FromResult(false));

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.Retired, result.Status);
        Assert.Equal(2, settingsReads);
    }

    [Fact]
    public async Task ExecuteAsync_UncertainPersistenceDelete_ReconcilesConfirmedAbsence()
    {
        var settingsReads = 0;
        var operations = CreateOperations(
            readSettings: (_, _) =>
            {
                settingsReads++;
                return Task.FromResult<ReactionRoleSettings?>(
                    settingsReads == 1 ? CreateSettings() : null);
            },
            deleteSettings: (_, _, _) =>
                throw new TimeoutException("Mongo delete acknowledgement timed out"));

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.Retired, result.Status);
        Assert.Equal(2, settingsReads);
        Assert.IsType<TimeoutException>(result.Failure);
    }

    [Fact]
    public void SourceParser_RejectsMalformedAndDuplicateRoleIds()
    {
        Assert.False(LegacyReactionRoleSourceParser.TryParse(
            new ReactionRoleSettings([], "1", "2", "3"),
            out _));
        Assert.False(LegacyReactionRoleSourceParser.TryParse(
            new ReactionRoleSettings(
                [new RoleEmotePair("4", "5"), new RoleEmotePair("4", "6")],
                "1",
                "2",
                "3"),
            out _));
        Assert.False(LegacyReactionRoleSourceParser.TryParse(
            new ReactionRoleSettings(
                [new RoleEmotePair("not-a-role", "5")],
                "1",
                "2",
                "3"),
            out _));
    }

    private static async Task AssertUnsafeSourceNeverDeletesAnythingAsync(
        LegacyReactionRolePanelLookupStatus lookupStatus)
    {
        var deletePanelCalls = 0;
        var deleteSettingsCalls = 0;
        var operations = CreateOperations(
            readSettings: (_, _) => Task.FromResult<ReactionRoleSettings?>(CreateSettings()),
            readPanel: (_, _) => Task.FromResult(
                new LegacyReactionRolePanelLookupResult(lookupStatus)),
            deletePanel: (_, _, _) =>
            {
                deletePanelCalls++;
                return Task.FromResult(true);
            },
            deleteSettings: (_, _, _) =>
            {
                deleteSettingsCalls++;
                return Task.FromResult(true);
            });

        var result = await LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
            MessageId,
            GuildId,
            operations,
            CancellationToken.None);

        Assert.Equal(LegacyReactionRoleRetirementStatus.UnsafeSource, result.Status);
        Assert.Equal(0, deletePanelCalls);
        Assert.Equal(0, deleteSettingsCalls);
    }

    private static LegacyReactionRoleRetirementOperations CreateOperations(
        Func<ulong, CancellationToken, Task<ReactionRoleSettings?>>? readSettings = null,
        Func<CancellationToken, Task<bool?>>? canManageRoles = null,
        Func<LegacyReactionRoleSource, CancellationToken, Task<LegacyReactionRolePanelLookupResult>>? readPanel = null,
        Func<LegacyReactionRolePanelSnapshot, IReadOnlyCollection<ulong>, CancellationToken, Task<bool>>? deletePanel = null,
        Func<ulong, ulong, CancellationToken, Task<bool>>? deleteSettings = null)
        => new(
            readSettings ?? ((_, _) => Task.FromResult<ReactionRoleSettings?>(CreateSettings())),
            canManageRoles ?? (_ => Task.FromResult<bool?>(true)),
            readPanel ?? ((source, _) => Task.FromResult(Found(source))),
            deletePanel ?? ((_, _, _) => Task.FromResult(true)),
            deleteSettings ?? ((_, _, _) => Task.FromResult(true)),
            () => false);

    private static LegacyReactionRolePanelLookupResult Found(
        LegacyReactionRoleSource source)
        => new(
            LegacyReactionRolePanelLookupStatus.Found,
            new LegacyReactionRolePanelSnapshot(
                source.GuildId,
                source.ChannelId,
                source.MessageId,
                99,
                []),
            "Games");

    private static ReactionRoleSettings CreateSettings(
        ulong guildId = GuildId,
        ulong channelId = ChannelId,
        ulong messageId = MessageId)
        => new(
            [new RoleEmotePair("4", "5")],
            guildId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            channelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            messageId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static LegacyReactionRoleSource Parse(ReactionRoleSettings settings)
    {
        Assert.True(LegacyReactionRoleSourceParser.TryParse(settings, out var source));
        return Assert.IsType<LegacyReactionRoleSource>(source);
    }
}
