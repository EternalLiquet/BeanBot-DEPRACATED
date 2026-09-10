using System.Reflection;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Discord;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuAuditServiceTests
{
    [Fact]
    public async Task AuditSpecific_Healthy_WhenCurrentStateMatchesSavedPanel()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.AuditAsync(
            Fixture.GuildId,
            Fixture.BotId,
            fixture.Settings[0].Id,
            CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(RoleMenuAuditStatus.Healthy, item.Status);
        Assert.False(result.RequestedMenuNotFound);
        Assert.False(result.PersistenceUnavailable);
        Assert.Equal(1, fixture.BotReads);
        Assert.Equal(1, fixture.ChannelReads);
        Assert.Equal(1, fixture.PanelReads);
    }

    [Fact]
    public async Task AuditSpecific_NotFound_DoesNotReadDiscord()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.AuditAsync(
            Fixture.GuildId,
            Fixture.BotId,
            ObjectId.GenerateNewId(),
            CancellationToken.None);

        Assert.True(result.RequestedMenuNotFound);
        Assert.Empty(result.Items);
        Assert.Equal(0, fixture.BotReads);
        Assert.Equal(0, fixture.ChannelReads);
        Assert.Equal(0, fixture.PanelReads);
    }

    [Fact]
    public async Task AuditSpecific_MissingChannel_IsBroken()
    {
        var fixture = new Fixture { Channel = null };

        var result = await fixture.AuditFirstAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(RoleMenuAuditStatus.Broken, item.Status);
        Assert.Contains("channel", item.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.PanelReads);
    }

    [Fact]
    public async Task AuditSpecific_MissingPanel_IsBroken()
    {
        var fixture = new Fixture { Panel = null };

        var result = await fixture.AuditFirstAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(RoleMenuAuditStatus.Broken, item.Status);
        Assert.Contains("message", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditSpecific_UnexpectedAuthor_IsBroken()
    {
        var fixture = new Fixture();
        fixture.PanelFactory = settings => new RoleMenuPanelSnapshot(
            Fixture.GuildId,
            Fixture.ChannelId,
            ulong.Parse(settings.MessageId),
            999UL,
            true);

        var result = await fixture.AuditFirstAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(RoleMenuAuditStatus.Broken, item.Status);
        Assert.Contains("authored", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditSpecific_DeletedRole_IsBrokenBeforeChannelRead()
    {
        var fixture = new Fixture
        {
            RoleValidation = new RoleMenuRoleValidationResult(
                [],
                [new RoleMenuRoleIssue(10UL, null, RoleMenuRoleIssueKind.Missing)])
        };

        var result = await fixture.AuditFirstAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(RoleMenuAuditStatus.Broken, item.Status);
        Assert.Contains("deleted", item.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.ChannelReads);
    }

    [Theory]
    [InlineData(RoleMenuRoleIssueKind.Managed, "managed")]
    [InlineData(RoleMenuRoleIssueKind.BotHierarchy, "highest role")]
    [InlineData(RoleMenuRoleIssueKind.BotMissingManageRoles, "Manage Roles")]
    public async Task AuditSpecific_UnassignableRoleState_IsBroken(
        RoleMenuRoleIssueKind issueKind,
        string expectedReason)
    {
        var fixture = new Fixture
        {
            RoleValidation = new RoleMenuRoleValidationResult(
                [],
                [new RoleMenuRoleIssue(10UL, "Role", issueKind)])
        };

        var result = await fixture.AuditFirstAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(RoleMenuAuditStatus.Broken, item.Status);
        Assert.Contains(expectedReason, item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditSpecific_MissingChannelPermission_IsBrokenBeforePanelRead()
    {
        var fixture = new Fixture
        {
            PermissionFailure =
                "Bean Bot is missing these permissions in the target channel: **Read Message History**."
        };

        var result = await fixture.AuditFirstAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(RoleMenuAuditStatus.Broken, item.Status);
        Assert.Contains("Read Message History", item.Reason, StringComparison.Ordinal);
        Assert.Equal(0, fixture.PanelReads);
    }

    [Fact]
    public async Task AuditBulk_TransientLookupFailure_IsUnknownAndLaterMenusStillComplete()
    {
        var fixture = new Fixture(menuCount: 2);
        var firstMenuId = fixture.Settings[0].Id;
        fixture.ChannelReader = (_, _, _) =>
        {
            fixture.ChannelReads++;
            return fixture.ChannelReads == 1
                ? Task.FromException<ITextChannel?>(new TimeoutException("Discord lookup timed out."))
                : Task.FromResult<ITextChannel?>(fixture.Channel);
        };

        var result = await fixture.Service.AuditAsync(
            Fixture.GuildId,
            Fixture.BotId,
            null,
            CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(RoleMenuAuditStatus.Unknown,
            result.Items.Single(item => item.MenuId == firstMenuId).Status);
        Assert.Equal(RoleMenuAuditStatus.Healthy,
            result.Items.Single(item => item.MenuId != firstMenuId).Status);
        Assert.Equal(2, fixture.ChannelReads);
        Assert.Equal(1, fixture.PanelReads);
    }

    [Fact]
    public async Task AuditBulk_IsHardCappedAtTwentyFiveAndSequential()
    {
        var fixture = new Fixture(menuCount: RoleMenuConstants.MaximumListedMenus + 1);
        var activeReads = 0;
        var maximumActiveReads = 0;
        fixture.ChannelReader = async (_, _, cancellationToken) =>
        {
            fixture.ChannelReads++;
            var active = Interlocked.Increment(ref activeReads);
            maximumActiveReads = Math.Max(maximumActiveReads, active);
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return fixture.Channel;
            }
            finally
            {
                Interlocked.Decrement(ref activeReads);
            }
        };

        var result = await fixture.Service.AuditAsync(
            Fixture.GuildId,
            Fixture.BotId,
            null,
            CancellationToken.None);

        Assert.True(result.HasMore);
        Assert.Equal(RoleMenuConstants.MaximumListedMenus, result.Items.Count);
        Assert.Equal(RoleMenuConstants.MaximumListedMenus + 1, fixture.RequestedGuildLimit);
        Assert.Equal(RoleMenuConstants.MaximumListedMenus, fixture.ChannelReads);
        Assert.Equal(1, maximumActiveReads);
    }

    [Fact]
    public async Task AuditBulk_ParentCancellation_StopsStartingFurtherMenus()
    {
        var fixture = new Fixture(menuCount: 2);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ChannelReader = async (_, _, cancellationToken) =>
        {
            fixture.ChannelReads++;
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return fixture.Channel;
        };
        using var cancellation = new CancellationTokenSource();

        var audit = fixture.Service.AuditAsync(
            Fixture.GuildId,
            Fixture.BotId,
            null,
            cancellation.Token);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audit);
        Assert.Equal(1, fixture.ChannelReads);
        Assert.Equal(0, fixture.PanelReads);
    }

    [Fact]
    public async Task AuditSpecific_PersistenceFailure_IsUnknownWithoutDiscordReads()
    {
        var fixture = new Fixture();
        fixture.SettingsReader = (_, _, _) =>
            Task.FromException<RoleMenuSettings?>(new TimeoutException("Mongo timeout"));

        var result = await fixture.AuditFirstAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(RoleMenuAuditStatus.Unknown, item.Status);
        Assert.True(result.PersistenceUnavailable);
        Assert.Equal(0, fixture.BotReads);
        Assert.Equal(0, fixture.ChannelReads);
    }

    [Fact]
    public void Presentation_BulkResult_RemainsWithinDiscordContentLimit()
    {
        var items = Enumerable.Range(0, RoleMenuConstants.MaximumListedMenus)
            .Select(_ => new RoleMenuAuditItem(
                ObjectId.GenerateNewId(),
                RoleMenuAuditStatus.Broken,
                new string('x', 500)))
            .ToList();

        var content = RoleMenuAuditPresentation.Format(
            new RoleMenuAuditBatchResult(items, HasMore: true),
            null);

        Assert.True(content.Length <= RoleMenuConstants.MaximumResponseContentLength);
        Assert.Contains("25 newest", content, StringComparison.Ordinal);
    }

    private sealed class Fixture
    {
        internal const ulong GuildId = 1UL;
        internal const ulong BotId = 2UL;
        internal const ulong ChannelId = 4UL;

        private readonly IGuildUser _bot;

        internal Fixture(int menuCount = 1)
        {
            Settings = Enumerable.Range(0, menuCount)
                .Select(index => CreateSettings((ulong)(5 + index)))
                .ToList();
            _bot = Proxy<IGuildUser>((method, _) => method.Name switch
            {
                "get_Id" => BotId,
                _ => throw new NotSupportedException(method.Name)
            });
            Channel = Proxy<ITextChannel>((method, _) => throw new NotSupportedException(method.Name));
            PanelFactory = settings => new RoleMenuPanelSnapshot(
                GuildId,
                ChannelId,
                ulong.Parse(settings.MessageId),
                BotId,
                true);
            SettingsReader = (menuId, guildId, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<RoleMenuSettings?>(
                    Settings.FirstOrDefault(item => item.Id == menuId && item.GuildId == guildId.ToString()));
            };
            GuildSettingsReader = (guildId, maximumResults, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestedGuildLimit = maximumResults;
                return Task.FromResult<IReadOnlyList<RoleMenuSettings>>(
                    Settings.Where(item => item.GuildId == guildId.ToString())
                        .Take(maximumResults)
                        .ToList());
            };
            ChannelReader = (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ChannelReads++;
                return Task.FromResult(Channel);
            };

            Service = new RoleMenuAuditService(new RoleMenuAuditOperations(
                (menuId, guildId, cancellationToken) =>
                    SettingsReader(menuId, guildId, cancellationToken),
                (guildId, maximumResults, cancellationToken) =>
                    GuildSettingsReader(guildId, maximumResults, cancellationToken),
                (_, _, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BotReads++;
                    return Task.FromResult<IGuildUser?>(_bot);
                },
                (_, _) => RoleValidation,
                (guildId, channelId, cancellationToken) =>
                    ChannelReader(guildId, channelId, cancellationToken),
                (_, _) => PermissionFailure,
                (_, _, messageId, menuId, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PanelReads++;
                    var settings = Settings.Single(item => item.Id == menuId);
                    Assert.Equal(ulong.Parse(settings.MessageId), messageId);
                    return Task.FromResult(PanelFactory(settings));
                }));
        }

        internal RoleMenuAuditService Service { get; }
        internal List<RoleMenuSettings> Settings { get; }
        internal ITextChannel? Channel { get; set; }
        internal RoleMenuPanelSnapshot? Panel
        {
            set => PanelFactory = _ => value;
        }
        internal Func<RoleMenuSettings, RoleMenuPanelSnapshot?> PanelFactory { get; set; }
        internal RoleMenuRoleValidationResult RoleValidation { get; set; } =
            new([], []);
        internal string? PermissionFailure { get; set; }
        internal int BotReads { get; private set; }
        internal int ChannelReads { get; set; }
        internal int PanelReads { get; private set; }
        internal int RequestedGuildLimit { get; private set; }
        internal Func<ObjectId, ulong, CancellationToken, Task<RoleMenuSettings?>> SettingsReader { get; set; }
        internal Func<ulong, int, CancellationToken, Task<IReadOnlyList<RoleMenuSettings>>> GuildSettingsReader { get; set; }
        internal Func<ulong, ulong, CancellationToken, Task<ITextChannel?>> ChannelReader { get; set; }

        internal Task<RoleMenuAuditBatchResult> AuditFirstAsync()
            => Service.AuditAsync(GuildId, BotId, Settings[0].Id, CancellationToken.None);

        private static RoleMenuSettings CreateSettings(ulong messageId)
            => new(
                ObjectId.GenerateNewId(),
                GuildId.ToString(),
                ChannelId.ToString(),
                messageId.ToString(),
                "Roles",
                string.Empty,
                ["10"],
                RoleMenuSelectionMode.Multiple);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, DiscordProxy>();
        ((DiscordProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class DiscordProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler(targetMethod!, args ?? []);
    }
}
