using System.Reflection;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuMigrationServiceTests
{
    private const ulong GuildId = 1;
    private const ulong ChannelId = 10;
    private const ulong LegacyMessageId = 20;
    private const ulong AdministratorId = 30;
    private const ulong BotUserId = 40;

    [Fact]
    public async Task CreatePreviewAsync_MapsRecognizedLegacyPanelToMultipleRoleMenu()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId, LegacyMessageId, 4, 5));

        var result = await fixture.Service.CreatePreviewAsync(
            new RoleMenuMigrationRequest(
                LegacyMessageId,
                TargetChannelId: null,
                TargetChannelGuildId: null,
                TargetChannelType: null,
                Title: null,
                Description: null),
            GuildId,
            AdministratorId,
            BotUserId,
            CancellationToken.None);

        var draft = Assert.IsType<RoleMenuDraft>(result.Draft);
        Assert.Equal(RoleMenuSelectionMode.Multiple, draft.SelectionMode);
        Assert.Equal([4UL, 5UL], draft.RoleIds);
        Assert.Equal("Games", draft.Title);
        Assert.Equal(ChannelId, draft.TargetChannelId);
        Assert.Equal(LegacyMessageId, draft.LegacyReactionRoleMessageId);
        Assert.Equal(
            RoleMenuMigrationIdentity.CreateMenuId(GuildId, LegacyMessageId),
            draft.MenuId);
        Assert.Contains($"/{GuildId}/{ChannelId}/{LegacyMessageId}", result.SourceMessageLink, StringComparison.Ordinal);
        Assert.Equal(0, fixture.ReactionStore.InsertCalls);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task CreatePreviewAsync_RejectsCrossGuildSourceBeforeDiscordOrPublication()
    {
        var fixture = CreateFixture(CreateLegacySettings(999, ChannelId, LegacyMessageId, 4));

        var result = await fixture.Service.CreatePreviewAsync(
            new RoleMenuMigrationRequest(
                LegacyMessageId,
                TargetChannelId: null,
                TargetChannelGuildId: null,
                TargetChannelType: null,
                Title: null,
                Description: null),
            GuildId,
            AdministratorId,
            BotUserId,
            CancellationToken.None);

        Assert.Null(result.Draft);
        Assert.Contains("different server", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.DiscordUserReads);
        Assert.Equal(0, fixture.DiscordChannelReads);
        Assert.Equal(0, fixture.ReactionStore.InsertCalls);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task ConfirmAsync_ReloadsLegacyStateAndStopsWhenRolesChangedAfterPreview()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId, LegacyMessageId, 4, 5));
        var preview = await fixture.Service.CreatePreviewAsync(
            new RoleMenuMigrationRequest(
                LegacyMessageId,
                TargetChannelId: null,
                TargetChannelGuildId: null,
                TargetChannelType: null,
                Title: null,
                Description: null),
            GuildId,
            AdministratorId,
            BotUserId,
            CancellationToken.None);
        var draft = Assert.IsType<RoleMenuDraft>(preview.Draft);
        var readsAfterPreview = fixture.ReactionStore.GetCalls;

        fixture.ReactionStore.Settings = CreateLegacySettings(
            GuildId,
            ChannelId,
            LegacyMessageId,
            4,
            6);
        fixture.SourceChannel.Message = CreateLegacyMessage(
            LegacyMessageId,
            BotUserId,
            "Games",
            4,
            6);

        var result = await fixture.Service.ConfirmAsync(
            draft,
            AdministratorId,
            BotUserId,
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("changed after this preview", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.ReactionStore.GetCalls > readsAfterPreview);
        Assert.Equal(0, fixture.ReactionStore.InsertCalls);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task CreatePreviewAsync_WhenMigrationAlreadyPersisted_ReturnsExistingWithoutTouchingSource()
    {
        var fixture = CreateFixture(settings: null);
        var menuId = RoleMenuMigrationIdentity.CreateMenuId(GuildId, LegacyMessageId);
        var existing = new RoleMenuSettings(
            menuId,
            GuildId.ToString(),
            ChannelId.ToString(),
            "99",
            "Games",
            string.Empty,
            ["4", "5"],
            RoleMenuSelectionMode.Multiple,
            LegacyMessageId.ToString());
        fixture.RoleMenuStore.Settings = existing;

        var result = await fixture.Service.CreatePreviewAsync(
            new RoleMenuMigrationRequest(
                LegacyMessageId,
                TargetChannelId: null,
                TargetChannelGuildId: null,
                TargetChannelType: null,
                Title: null,
                Description: null),
            GuildId,
            AdministratorId,
            BotUserId,
            CancellationToken.None);

        Assert.Same(existing, result.ExistingMenu);
        Assert.Null(result.Draft);
        Assert.Contains("already migrated", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.ReactionStore.GetCalls);
        Assert.Equal(0, fixture.ReactionStore.InsertCalls);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    private static MigrationFixture CreateFixture(ReactionRoleSettings? settings)
    {
        var roles = new List<IRole>
        {
            CreateRole(1, "@everyone", 0),
            CreateRole(4, "Alpha", 10),
            CreateRole(5, "Beta", 20),
            CreateRole(6, "Gamma", 30),
            CreateRole(1000, "Bean Bot", 100),
            CreateRole(1001, "Administrator", 90)
        };
        var guild = CreateGuild(GuildId, roles, roles[0]);
        var bot = CreateGuildUser(BotUserId, guild, [1000]);
        var administrator = CreateGuildUser(AdministratorId, guild, [1001]);
        var sourceChannel = CreateTextChannel(
            GuildId,
            ChannelId,
            CreateLegacyMessage(LegacyMessageId, BotUserId, "Games", 4, 5));

        var reactionStore = new TrackingReactionRoleStore { Settings = settings };
        var reactionRepository = new ReactionRoleRepository(
            reactionStore,
            NullLogger<ReactionRoleRepository>.Instance);
        var roleMenuStore = new TrackingRoleMenuStore();
        var roleMenuRepository = new RoleMenuRepository(
            roleMenuStore,
            NullLogger<RoleMenuRepository>.Instance);
        var executionContext = new InteractionExecutionContext();
        var roleMenus = new RoleMenuInteractionService(
            roleMenuRepository,
            new RoleMenuDraftRegistry(),
            new RoleMenuMutationCoordinator(),
            executionContext);

        var discordUserReads = 0;
        var discordChannelReads = 0;
        var discord = new DiscordRoleMenuClient(
            (guildId, userId, _) =>
            {
                discordUserReads++;
                IGuildUser? user = guildId != GuildId
                    ? null
                    : userId switch
                    {
                        BotUserId => bot,
                        AdministratorId => administrator,
                        _ => null
                    };
                return Task.FromResult(user);
            },
            (channelId, _) =>
            {
                discordChannelReads++;
                IChannel? channel = channelId == ChannelId ? sourceChannel.Channel : null;
                return Task.FromResult(channel);
            });
        var administration = new RoleMenuAdministrationService(
            roleMenus,
            discord,
            NullLogger<RoleMenuAdministrationService>.Instance);
        var legacyDiscord = new LegacyReactionRoleMigrationClient(
            (channelId, _) =>
            {
                IChannel? channel = channelId == ChannelId ? sourceChannel.Channel : null;
                return Task.FromResult(channel);
            });
        var service = new RoleMenuMigrationService(
            reactionRepository,
            roleMenus,
            discord,
            legacyDiscord,
            administration);

        return new MigrationFixture(
            service,
            reactionStore,
            roleMenuStore,
            sourceChannel,
            () => discordUserReads,
            () => discordChannelReads);
    }

    private static ReactionRoleSettings CreateLegacySettings(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        params ulong[] roleIds)
        => new(
            [.. roleIds.Select((roleId, index) => new RoleEmotePair(
                roleId.ToString(),
                $"emoji-{index}"))],
            guildId.ToString(),
            channelId.ToString(),
            messageId.ToString());

    private static IMessage CreateLegacyMessage(
        ulong messageId,
        ulong authorId,
        string title,
        params ulong[] roleIds)
    {
        var embed = new EmbedBuilder().WithFooter($"Role Group: {title}");
        foreach (var roleId in roleIds)
        {
            embed.AddField("role", $"<@&{roleId}>");
        }

        var message = DispatchProxy.Create<IMessage, MessageProxy>();
        var proxy = (MessageProxy)message;
        proxy.Id = messageId;
        proxy.Author = CreateUser(authorId);
        proxy.Embeds = [embed.Build()];
        return message;
    }

    private static IUser CreateUser(ulong id)
    {
        var user = DispatchProxy.Create<IUser, UserProxy>();
        ((UserProxy)user).Id = id;
        return user;
    }

    private static IRole CreateRole(ulong id, string name, int position)
    {
        var role = DispatchProxy.Create<IRole, RoleProxy>();
        var proxy = (RoleProxy)role;
        proxy.Id = id;
        proxy.Name = name;
        proxy.Position = position;
        return role;
    }

    private static IGuild CreateGuild(
        ulong id,
        IReadOnlyCollection<IRole> roles,
        IRole everyoneRole)
    {
        var guild = DispatchProxy.Create<IGuild, GuildProxy>();
        var proxy = (GuildProxy)guild;
        proxy.Id = id;
        proxy.Roles = roles;
        proxy.EveryoneRole = everyoneRole;
        proxy.OwnerId = 5000;
        return guild;
    }

    private static IGuildUser CreateGuildUser(
        ulong id,
        IGuild guild,
        IReadOnlyCollection<ulong> roleIds)
    {
        var user = DispatchProxy.Create<IGuildUser, GuildUserProxy>();
        var proxy = (GuildUserProxy)user;
        proxy.Id = id;
        proxy.Guild = guild;
        proxy.RoleIds = roleIds;
        return user;
    }

    private static TextChannelProxy CreateTextChannel(
        ulong guildId,
        ulong channelId,
        IMessage message)
    {
        var channel = DispatchProxy.Create<ITextChannel, TextChannelProxy>();
        var proxy = (TextChannelProxy)channel;
        proxy.Channel = channel;
        proxy.GuildId = guildId;
        proxy.Id = channelId;
        proxy.Message = message;
        return proxy;
    }

    private sealed record MigrationFixture(
        RoleMenuMigrationService Service,
        TrackingReactionRoleStore ReactionStore,
        TrackingRoleMenuStore RoleMenuStore,
        TextChannelProxy SourceChannel,
        Func<int> UserReads,
        Func<int> ChannelReads)
    {
        internal int DiscordUserReads => UserReads();
        internal int DiscordChannelReads => ChannelReads();
    }

    private sealed class TrackingReactionRoleStore : IReactionRoleSettingsStore
    {
        public ReactionRoleSettings? Settings { get; set; }
        public int GetCalls { get; private set; }
        public int InsertCalls { get; private set; }

        public Task InsertAsync(ReactionRoleSettings roleSettings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InsertCalls++;
            Settings = roleSettings;
            return Task.CompletedTask;
        }

        public Task<List<ReactionRoleSettings>> GetRecentAsync(
            DateTime oldestLastAccessedUtc,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new List<ReactionRoleSettings>());
        }

        public Task<ReactionRoleSettings?> GetByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return Task.FromResult(
                string.Equals(Settings?.MessageId, messageId, StringComparison.Ordinal)
                    ? Settings
                    : null);
        }
    }

    private sealed class TrackingRoleMenuStore : IRoleMenuStore
    {
        public RoleMenuSettings? Settings { get; set; }
        public int UpsertCalls { get; private set; }

        public Task UpsertAsync(RoleMenuSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpsertCalls++;
            Settings = settings;
            return Task.CompletedTask;
        }

        public Task<RoleMenuSettings?> GetByIdAsync(
            ObjectId id,
            string guildId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Settings?.Id == id
                && string.Equals(Settings.GuildId, guildId, StringComparison.Ordinal)
                    ? Settings
                    : null);
        }

        public Task<List<RoleMenuSettings>> GetByGuildAsync(
            string guildId,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<RoleMenuSettings> matches = Settings is not null
                && string.Equals(Settings.GuildId, guildId, StringComparison.Ordinal)
                ? [Settings]
                : [];
            return Task.FromResult(matches.Take(maximumResults).ToList());
        }

        public Task<bool> DeleteAsync(
            ObjectId id,
            string guildId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }

    public class RoleProxy : DispatchProxy
    {
        public ulong Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Position { get; set; }
        public bool IsManaged { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_Id" => Id,
                "get_Name" => Name,
                "get_Position" => Position,
                "get_IsManaged" => IsManaged,
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    public class GuildProxy : DispatchProxy
    {
        public ulong Id { get; set; }
        public ulong OwnerId { get; set; }
        public IReadOnlyCollection<IRole> Roles { get; set; } = [];
        public IRole EveryoneRole { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_Id" => Id,
                "get_OwnerId" => OwnerId,
                "get_Roles" => Roles,
                "get_EveryoneRole" => EveryoneRole,
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    public class GuildUserProxy : DispatchProxy
    {
        private const ulong ManageRoles = 1UL << 28;
        private const ulong RequiredChannelPermissions =
            (1UL << 10) | (1UL << 11) | (1UL << 14) | (1UL << 16);

        public ulong Id { get; set; }
        public IGuild Guild { get; set; } = null!;
        public IReadOnlyCollection<ulong> RoleIds { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_Id" => Id,
                "get_Guild" => Guild,
                "get_RoleIds" => RoleIds,
                "get_GuildPermissions" => new GuildPermissions(ManageRoles),
                nameof(IGuildUser.GetPermissions) => new ChannelPermissions(RequiredChannelPermissions),
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    public class TextChannelProxy : DispatchProxy
    {
        public ITextChannel Channel { get; set; } = null!;
        public ulong GuildId { get; set; }
        public ulong Id { get; set; }
        public IMessage Message { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_Id" => Id,
                "get_GuildId" => GuildId,
                "get_ChannelType" => ChannelType.Text,
                nameof(IMessageChannel.GetMessageAsync) => Task.FromResult<IMessage?>(
                    args is [ulong messageId, ..] && messageId == Message.Id ? Message : null),
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
        }
    }

    public class MessageProxy : DispatchProxy
    {
        public ulong Id { get; set; }
        public IUser Author { get; set; } = null!;
        public IReadOnlyCollection<IEmbed> Embeds { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_Id" => Id,
                "get_Author" => Author,
                "get_Embeds" => Embeds,
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    public class UserProxy : DispatchProxy
    {
        public ulong Id { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_Id"
                ? Id
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
