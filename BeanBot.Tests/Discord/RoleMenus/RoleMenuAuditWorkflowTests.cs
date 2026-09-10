using System.Globalization;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuAuditWorkflowTests
{
    private const ulong GuildId = 101UL;
    private const ulong BotUserId = 202UL;
    private const ulong ChannelId = 303UL;
    private const ulong MessageId = 404UL;
    private const ulong RoleId = 505UL;
    private static readonly ObjectId MenuId =
        ObjectId.Parse("64e7611aaac75f172f0f9876");

    [Fact]
    public async Task ExecuteAsync_HealthyPanel_PassesEveryReadOnlyCheck()
    {
        var environmentReads = 0;
        var panelReads = 0;
        var operations = CreateOperations(
            readEnvironment: (_, _, _, _) =>
            {
                environmentReads++;
                return Task.FromResult(CreateEnvironment());
            },
            readPanel: (_, _, _, _, _) =>
            {
                panelReads++;
                return Task.FromResult<RoleMenuPanelSnapshot?>(CreatePanel());
            });

        var result = await RoleMenuAuditWorkflow.ExecuteAsync(
            CreateSettings(),
            GuildId,
            BotUserId,
            operations,
            CancellationToken.None);

        Assert.Equal(RoleMenuAuditStatus.Healthy, result.Status);
        Assert.Empty(result.Findings);
        Assert.Equal(1, environmentReads);
        Assert.Equal(1, panelReads);
    }

    [Fact]
    public async Task ExecuteAsync_GuildMismatch_IsBrokenWithoutDiscordReads()
    {
        var environmentReads = 0;
        var panelReads = 0;
        var operations = CreateOperations(
            readEnvironment: (_, _, _, _) =>
            {
                environmentReads++;
                return Task.FromResult(CreateEnvironment());
            },
            readPanel: (_, _, _, _, _) =>
            {
                panelReads++;
                return Task.FromResult<RoleMenuPanelSnapshot?>(CreatePanel());
            });
        var settings = CreateSettings(guildId: GuildId + 1);

        var result = await RoleMenuAuditWorkflow.ExecuteAsync(
            settings,
            GuildId,
            BotUserId,
            operations,
            CancellationToken.None);

        AssertBroken(result, RoleMenuAuditIssueKind.GuildMismatch);
        Assert.Equal(0, environmentReads);
        Assert.Equal(0, panelReads);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidSavedSettings_AreBrokenWithoutDiscordReads()
    {
        var settings = CreateSettings();
        settings = new RoleMenuSettings(
            settings.Id,
            settings.GuildId,
            "not-a-channel",
            settings.MessageId,
            settings.Title,
            settings.Description,
            settings.RoleIds,
            settings.SelectionMode);
        var environmentReads = 0;
        var operations = CreateOperations(
            readEnvironment: (_, _, _, _) =>
            {
                environmentReads++;
                return Task.FromResult(CreateEnvironment());
            });

        var result = await RoleMenuAuditWorkflow.ExecuteAsync(
            settings,
            GuildId,
            BotUserId,
            operations,
            CancellationToken.None);

        AssertBroken(result, RoleMenuAuditIssueKind.SettingsInvalid);
        Assert.Equal(0, environmentReads);
    }

    [Fact]
    public async Task ExecuteAsync_MissingChannel_IsBrokenWithoutPanelRead()
    {
        var panelReads = 0;
        var operations = CreateOperations(
            readEnvironment: (_, _, _, _) => Task.FromResult(
                new RoleMenuAuditEnvironmentResult(
                    RoleMenuAuditEnvironmentStatus.ChannelMissingOrInvalid)),
            readPanel: (_, _, _, _, _) =>
            {
                panelReads++;
                return Task.FromResult<RoleMenuPanelSnapshot?>(CreatePanel());
            });

        var result = await ExecuteAsync(operations);

        AssertBroken(result, RoleMenuAuditIssueKind.ChannelMissingOrInvalid);
        Assert.Equal(0, panelReads);
    }

    [Fact]
    public async Task ExecuteAsync_MissingPanel_IsBroken()
    {
        var operations = CreateOperations(
            readPanel: (_, _, _, _, _) =>
                Task.FromResult<RoleMenuPanelSnapshot?>(null));

        var result = await ExecuteAsync(operations);

        AssertBroken(result, RoleMenuAuditIssueKind.PanelMissing);
    }

    [Theory]
    [InlineData(false, BotUserId, RoleMenuAuditIssueKind.PanelMissingManageButton)]
    [InlineData(true, BotUserId + 1, RoleMenuAuditIssueKind.PanelUnexpectedAuthor)]
    public async Task ExecuteAsync_PanelIdentityDrift_IsBrokenAndNeverMutated(
        bool hasManageButton,
        ulong authorId,
        RoleMenuAuditIssueKind expectedIssue)
    {
        var panel = CreatePanel(authorId: authorId, hasManageButton: hasManageButton);
        var panelReads = 0;
        var operations = CreateOperations(
            readPanel: (_, _, _, _, _) =>
            {
                panelReads++;
                return Task.FromResult<RoleMenuPanelSnapshot?>(panel);
            });

        var result = await ExecuteAsync(operations);

        AssertBroken(result, expectedIssue);
        Assert.Equal(1, panelReads);
    }

    [Fact]
    public async Task ExecuteAsync_DeletedConfiguredRole_IsBroken()
    {
        var environment = CreateEnvironment(roles: []);
        var operations = CreateOperations(
            readEnvironment: (_, _, _, _) => Task.FromResult(environment));

        var result = await ExecuteAsync(operations);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(RoleMenuAuditStatus.Broken, result.Status);
        Assert.Equal(RoleMenuAuditIssueKind.RoleMissing, finding.Kind);
        Assert.Equal(RoleId, finding.RoleId);
    }

    [Theory]
    [InlineData(true, 10, 100, RoleMenuAuditIssueKind.RoleManaged)]
    [InlineData(false, 100, 100, RoleMenuAuditIssueKind.RoleBotHierarchy)]
    public async Task ExecuteAsync_UnassignableConfiguredRole_IsBroken(
        bool isManaged,
        int rolePosition,
        int botHierarchy,
        RoleMenuAuditIssueKind expectedIssue)
    {
        var roles = new[]
        {
            new RoleMenuRoleSnapshot(
                RoleId,
                "Configured role",
                IsEveryone: false,
                IsManaged: isManaged,
                Position: rolePosition)
        };
        var environment = CreateEnvironment(
            roles,
            new RoleMenuActorSnapshot(
                CanManageRoles: true,
                Hierarchy: botHierarchy,
                IsGuildOwner: false));
        var operations = CreateOperations(
            readEnvironment: (_, _, _, _) => Task.FromResult(environment));

        var result = await ExecuteAsync(operations);

        Assert.Contains(result.Findings, finding => finding.Kind == expectedIssue);
        Assert.Equal(RoleMenuAuditStatus.Broken, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_MissingManageRoles_IsBrokenWithoutAdministratorHierarchyInput()
    {
        var environment = CreateEnvironment(
            actor: new RoleMenuActorSnapshot(
                CanManageRoles: false,
                Hierarchy: 100,
                IsGuildOwner: false));
        var operations = CreateOperations(
            readEnvironment: (_, _, _, _) => Task.FromResult(environment));

        var result = await ExecuteAsync(operations);

        Assert.Contains(
            result.Findings,
            finding => finding.Kind == RoleMenuAuditIssueKind.BotMissingManageRoles);
        Assert.DoesNotContain(
            result.Findings,
            finding => finding.Kind is RoleMenuAuditIssueKind.SettingsInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_MissingChannelPermissions_ReportsEachRequiredPermission()
    {
        var environment = CreateEnvironment(
            channel: new RoleMenuAuditChannelSnapshot(
                GuildId,
                ChannelId,
                ViewChannel: false,
                SendMessages: false,
                EmbedLinks: false,
                ReadMessageHistory: false));
        var operations = CreateOperations(
            readEnvironment: (_, _, _, _) => Task.FromResult(environment));

        var result = await ExecuteAsync(operations);

        Assert.Equal(RoleMenuAuditStatus.Broken, result.Status);
        Assert.Contains(result.Findings, finding => finding.Kind == RoleMenuAuditIssueKind.MissingViewChannel);
        Assert.Contains(result.Findings, finding => finding.Kind == RoleMenuAuditIssueKind.MissingSendMessages);
        Assert.Contains(result.Findings, finding => finding.Kind == RoleMenuAuditIssueKind.MissingEmbedLinks);
        Assert.Contains(result.Findings, finding => finding.Kind == RoleMenuAuditIssueKind.MissingReadMessageHistory);
    }

    [Fact]
    public async Task AuditAsync_BoundedLookupTimeout_IsUnknownAndDoesNotRetry()
    {
        var environmentReads = 0;
        var operations = CreateOperations(
            readEnvironment: async (_, _, _, cancellationToken) =>
            {
                environmentReads++;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CreateEnvironment();
            });

        var result = await RoleMenuAuditor.AuditAsync(
            CreateSettings(),
            GuildId,
            BotUserId,
            operations,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.Equal(RoleMenuAuditStatus.Unknown, result.Status);
        Assert.Equal(RoleMenuAuditIssueKind.LookupTimedOut, Assert.Single(result.Findings).Kind);
        Assert.Equal(1, environmentReads);
    }

    [Fact]
    public async Task AuditAsync_UnexpectedDiscordFailure_IsUnknownAndDoesNotRetry()
    {
        var environmentReads = 0;
        var operations = CreateOperations(
            readEnvironment: (_, _, _, _) =>
            {
                environmentReads++;
                return Task.FromException<RoleMenuAuditEnvironmentResult>(
                    new InvalidOperationException("Discord lookup failed"));
            });

        var result = await RoleMenuAuditor.AuditAsync(
            CreateSettings(),
            GuildId,
            BotUserId,
            operations,
            NullLogger.Instance,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(RoleMenuAuditStatus.Unknown, result.Status);
        Assert.Equal(RoleMenuAuditIssueKind.LookupFailed, Assert.Single(result.Findings).Kind);
        Assert.Equal(1, environmentReads);
    }

    [Fact]
    public async Task AuditManyAsync_HardCapsMenusAndConcurrentDiscordReads()
    {
        var activeReads = 0;
        var maximumActiveReads = 0;
        var environmentReads = 0;
        var operations = CreateOperations(
            readEnvironment: async (_, _, _, cancellationToken) =>
            {
                Interlocked.Increment(ref environmentReads);
                var active = Interlocked.Increment(ref activeReads);
                UpdateMaximum(ref maximumActiveReads, active);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                    return CreateEnvironment();
                }
                finally
                {
                    Interlocked.Decrement(ref activeReads);
                }
            });
        var settings = Enumerable.Range(0, RoleMenuConstants.MaximumListedMenus + 5)
            .Select(_ => CreateSettings(ObjectId.GenerateNewId()))
            .ToList();

        var results = await RoleMenuAuditor.AuditManyAsync(
            settings,
            GuildId,
            BotUserId,
            operations,
            NullLogger.Instance,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(RoleMenuConstants.MaximumListedMenus, results.Count);
        Assert.Equal(RoleMenuConstants.MaximumListedMenus, environmentReads);
        Assert.InRange(maximumActiveReads, 1, RoleMenuAuditor.MaximumConcurrentAudits);
        Assert.All(results, result => Assert.Equal(RoleMenuAuditStatus.Healthy, result.Status));
    }

    [Fact]
    public async Task AuditAsync_ShutdownCancellation_PropagatesInsteadOfBecomingUnknown()
    {
        var operations = CreateOperations(
            readEnvironment: async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CreateEnvironment();
            });
        using var shutdown = new CancellationTokenSource();
        var audit = RoleMenuAuditor.AuditAsync(
            CreateSettings(),
            GuildId,
            BotUserId,
            operations,
            NullLogger.Instance,
            TimeSpan.FromSeconds(5),
            shutdown.Token);

        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audit);
    }

    [Fact]
    public void BuildEmbed_BulkAuditIncludesStableIdsAndOlderMenuGuidance()
    {
        var results = Enumerable.Range(0, RoleMenuConstants.MaximumListedMenus)
            .Select(_ => new RoleMenuAuditResult(
                ObjectId.GenerateNewId(),
                RoleMenuAuditStatus.Healthy,
                []))
            .ToList();

        var embed = RoleMenuAuditPresentation.BuildEmbed(results, hasMore: true);

        Assert.NotNull(embed.Description);
        Assert.Contains(results[0].MenuId.ToString(), embed.Description, StringComparison.Ordinal);
        Assert.Contains("newest saved menus", embed.Description, StringComparison.Ordinal);
        Assert.Contains("/role-menu audit", embed.Description, StringComparison.Ordinal);
        Assert.True(embed.Description.Length <= 4096);
    }

    private static Task<RoleMenuAuditResult> ExecuteAsync(RoleMenuAuditOperations operations)
        => RoleMenuAuditWorkflow.ExecuteAsync(
            CreateSettings(),
            GuildId,
            BotUserId,
            operations,
            CancellationToken.None);

    private static RoleMenuAuditOperations CreateOperations(
        Func<ulong, ulong, ulong, CancellationToken, Task<RoleMenuAuditEnvironmentResult>>? readEnvironment = null,
        Func<ulong, ObjectId, ulong, ulong, CancellationToken, Task<RoleMenuPanelSnapshot?>>? readPanel = null)
        => new(
            readEnvironment ?? ((_, _, _, _) => Task.FromResult(CreateEnvironment())),
            readPanel ?? ((_, _, _, _, _) => Task.FromResult<RoleMenuPanelSnapshot?>(CreatePanel())));

    private static RoleMenuAuditEnvironmentResult CreateEnvironment(
        IReadOnlyList<RoleMenuRoleSnapshot>? roles = null,
        RoleMenuActorSnapshot? actor = null,
        RoleMenuAuditChannelSnapshot? channel = null)
        => new(
            RoleMenuAuditEnvironmentStatus.Found,
            new RoleMenuBotSnapshot(
                GuildId,
                BotUserId,
                roles ??
                [
                    new RoleMenuRoleSnapshot(
                        RoleId,
                        "Configured role",
                        IsEveryone: false,
                        IsManaged: false,
                        Position: 10)
                ],
                actor ?? new RoleMenuActorSnapshot(
                    CanManageRoles: true,
                    Hierarchy: 100,
                    IsGuildOwner: false)),
            channel ?? new RoleMenuAuditChannelSnapshot(
                GuildId,
                ChannelId,
                ViewChannel: true,
                SendMessages: true,
                EmbedLinks: true,
                ReadMessageHistory: true));

    private static RoleMenuPanelSnapshot CreatePanel(
        ulong authorId = BotUserId,
        bool hasManageButton = true)
        => new(
            GuildId,
            ChannelId,
            MessageId,
            authorId,
            hasManageButton);

    private static RoleMenuSettings CreateSettings(
        ObjectId? menuId = null,
        ulong guildId = GuildId)
        => new(
            menuId ?? MenuId,
            guildId.ToString(CultureInfo.InvariantCulture),
            ChannelId.ToString(CultureInfo.InvariantCulture),
            MessageId.ToString(CultureInfo.InvariantCulture),
            "Audit test panel",
            "",
            [RoleId.ToString(CultureInfo.InvariantCulture)],
            RoleMenuSelectionMode.Multiple);

    private static void AssertBroken(
        RoleMenuAuditResult result,
        RoleMenuAuditIssueKind expectedIssue)
    {
        Assert.Equal(RoleMenuAuditStatus.Broken, result.Status);
        Assert.Contains(result.Findings, finding => finding.Kind == expectedIssue);
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref target, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
