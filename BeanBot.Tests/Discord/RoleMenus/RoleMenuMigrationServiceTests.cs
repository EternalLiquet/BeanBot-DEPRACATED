using System.Globalization;
using System.Net;
using System.Reflection;
using BeanBot.Discord.Interactions;
using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.Net;
using Discord.WebSocket;
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
    public void MigrationClient_RejectsMissingDiscordClient()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LegacyReactionRoleMigrationClient((DiscordSocketClient)null!));
    }

    [Fact]
    public void MigrationClient_RejectsMissingChannelReader()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LegacyReactionRoleMigrationClient(
                (Func<ulong, RequestOptions, Task<IChannel?>>)null!));
    }

    [Fact]
    public void MigrationClient_AcceptsDiscordClientWithoutConnecting()
    {
        using var discordClient = new DiscordSocketClient();

        Assert.NotNull(new LegacyReactionRoleMigrationClient(discordClient));
    }

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
    public async Task ConfirmAsync_RejectsReplacementSourceWithSameRoleIds()
    {
        var original = CreateLegacySettings(GuildId, ChannelId, LegacyMessageId, 4, 5);
        var fixture = CreateFixture(original);
        var preview = await fixture.Service.CreatePreviewAsync(
            new RoleMenuMigrationRequest(LegacyMessageId, null, null, null, null, null),
            GuildId, AdministratorId, BotUserId, CancellationToken.None);
        var draft = Assert.IsType<RoleMenuDraft>(preview.Draft);

        var replacement = new ReactionRoleSettings(
            [new RoleEmotePair("4", "emoji-0"),
                new RoleEmotePair("5", "emoji-1")],
            GuildId.ToString(CultureInfo.InvariantCulture),
            ChannelId.ToString(CultureInfo.InvariantCulture),
            LegacyMessageId.ToString(CultureInfo.InvariantCulture))
        { Id = ObjectId.GenerateNewId() };
        fixture.ReactionStore.Settings = replacement;

        var result = await fixture.Service.ConfirmAsync(
            draft, AdministratorId, BotUserId, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("changed after this preview", result.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task CreatePreviewAsync_RejectsInvisibleExplicitTitle()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5));

        var result = await fixture.Service.CreatePreviewAsync(
            new RoleMenuMigrationRequest(LegacyMessageId, null, null, null,
                "\u2800", null), GuildId, AdministratorId, BotUserId,
            CancellationToken.None);

        Assert.Null(result.Draft);
        Assert.Contains("visible `title`", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message ID", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PickerSelection_CarriesCustomTitleDescriptionAndTargetIntoPreview()
    {
        var fixture = CreateFixture(CreateLegacySettings(
            GuildId, ChannelId, LegacyMessageId, 4, 5));
        Assert.True(fixture.RoleMenus.CreateMigrationSelection(
            GuildId, AdministratorId, ChannelId, GuildId, ChannelType.Text,
            "Custom games", "Choose your favorites", out var created));
        var selection = Assert.IsType<RoleMenuMigrationSelection>(created);
        Assert.True(fixture.RoleMenus.TryGetMigrationSelection(
            selection.Id, GuildId, AdministratorId, out var chosen));

        var preview = await PreviewAsync(fixture, new RoleMenuMigrationRequest(
            LegacyMessageId, chosen!.TargetChannelId,
            chosen.TargetChannelGuildId, chosen.TargetChannelType,
            chosen.Title, chosen.Description));

        var draft = Assert.IsType<RoleMenuDraft>(preview.Draft);
        Assert.Equal(ChannelId, draft.TargetChannelId);
        Assert.Equal("Custom games", draft.Title);
        Assert.Equal("Choose your favorites", draft.Description);
    }

    [Fact]
    public async Task InvalidTitle_CanBeRecoveredThroughNewPickerSelectionWithoutMessageIdEntry()
    {
        var fixture = CreateFixture(CreateLegacySettings(
            GuildId, ChannelId, LegacyMessageId, 4, 5));
        var bad = await PreviewAsync(fixture, new RoleMenuMigrationRequest(
            LegacyMessageId, null, null, null, "\u2800", null));
        Assert.Null(bad.Draft);
        Assert.Contains("Run `/role-menu migrate` again", bad.Content);

        Assert.True(fixture.RoleMenus.CreateMigrationSelection(
            GuildId, AdministratorId, null, null, null,
            "Visible title", null, out var created));
        var selection = Assert.IsType<RoleMenuMigrationSelection>(created);
        var retry = await PreviewAsync(fixture, new RoleMenuMigrationRequest(
            LegacyMessageId, selection.TargetChannelId,
            selection.TargetChannelGuildId, selection.TargetChannelType,
            selection.Title, selection.Description));

        Assert.Equal("Visible title", Assert.IsType<RoleMenuDraft>(retry.Draft).Title);
    }

    [Fact]
    public async Task CreatePreviewAsync_WhenMigrationAlreadyPersisted_ReturnsExistingWithoutTouchingSource()
    {
        var fixture = CreateFixture(settings: null);
        var menuId = RoleMenuMigrationIdentity.CreateMenuId(GuildId, LegacyMessageId);
        var existing = new RoleMenuSettings(
            menuId,
            GuildId.ToString(CultureInfo.InvariantCulture),
            ChannelId.ToString(CultureInfo.InvariantCulture),
            "99",
            "Games",
            string.Empty,
            ["4", "5"],
            RoleMenuSelectionMode.Multiple,
            LegacyMessageId.ToString(CultureInfo.InvariantCulture));
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
        Assert.Contains("[View menu](https://discord.com/channels/",
            result.Content, StringComparison.Ordinal);
        Assert.Equal(0, fixture.ReactionStore.GetCalls);
        Assert.Equal(0, fixture.ReactionStore.InsertCalls);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task CreatePreviewAsync_RejectsMissingSourceAndUnrecognizedPanel()
    {
        var missing = CreateFixture(null);
        var missingResult = await PreviewAsync(missing);
        Assert.Null(missingResult.Draft);
        Assert.Equal(0, missing.RoleMenuStore.UpsertCalls);

        var unrecognized = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4));
        var unrecognizedResult = await PreviewAsync(unrecognized);
        Assert.Null(unrecognizedResult.Draft);
        Assert.Contains("does not positively match", unrecognizedResult.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, unrecognized.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task CreatePreviewAsync_RejectsInvalidTargetAndOversizedText()
    {
        var settings = CreateLegacySettings(GuildId, ChannelId, LegacyMessageId, 4, 5);
        var wrongGuild = CreateFixture(settings);
        var wrongGuildResult = await PreviewAsync(wrongGuild,
            new RoleMenuMigrationRequest(LegacyMessageId, ChannelId, 999,
                ChannelType.Text, null, null));
        Assert.Null(wrongGuildResult.Draft);
        Assert.Contains("normal text channel", wrongGuildResult.Content,
            StringComparison.OrdinalIgnoreCase);

        var missingChannel = CreateFixture(settings);
        var missingChannelResult = await PreviewAsync(missingChannel,
            new RoleMenuMigrationRequest(LegacyMessageId, 999, GuildId,
                ChannelType.Text, null, null));
        Assert.Null(missingChannelResult.Draft);
        Assert.Contains("target channel", missingChannelResult.Content,
            StringComparison.OrdinalIgnoreCase);

        var longTitle = await PreviewAsync(CreateFixture(settings),
            new RoleMenuMigrationRequest(LegacyMessageId, null, null, null,
                new string('A', RoleMenuConstants.MaximumTitleLength + 1), null));
        Assert.Null(longTitle.Draft);
        var longDescription = await PreviewAsync(CreateFixture(settings),
            new RoleMenuMigrationRequest(LegacyMessageId, null, null, null,
                "Games", new string('A', RoleMenuConstants.MaximumDescriptionLength + 1)));
        Assert.Null(longDescription.Draft);
    }

    [Fact]
    public async Task CreatePreviewAsync_RejectsLostBotPermissionAndMalformedRoles()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5));
        fixture.Bot.CanManageRoles = false;
        var denied = await PreviewAsync(fixture);
        Assert.Null(denied.Draft);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);

        var malformed = CreateFixture(new ReactionRoleSettings(
            [new RoleEmotePair("invalid", "emoji")], "1", "10", "20"));
        var invalid = await PreviewAsync(malformed);
        Assert.Null(invalid.Draft);
        Assert.Contains("malformed role", invalid.Content,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmAsync_ExistingMigrationCompletesWithoutPublishingAgain()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5));
        var draft = Assert.IsType<RoleMenuDraft>((await PreviewAsync(fixture)).Draft);
        fixture.RoleMenuStore.Settings = new RoleMenuSettings(
            draft.MenuId, "1", "10", "99", "Games", "", ["4", "5"],
            RoleMenuSelectionMode.Multiple, "20");

        var result = await fixture.Service.ConfirmAsync(draft,
            AdministratorId, BotUserId, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Contains("already migrated", result.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task CreatePreviewAsync_DistinguishesMissingSourceChannelAndMessage()
    {
        var missingChannel = CreateFixture(CreateLegacySettings(GuildId, 999,
            LegacyMessageId, 4, 5));
        var channelResult = await PreviewAsync(missingChannel);
        Assert.Null(channelResult.Draft);
        Assert.Contains("source channel no longer exists", channelResult.Content,
            StringComparison.OrdinalIgnoreCase);

        var missingMessage = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5));
        missingMessage.SourceChannel.Message = CreateLegacyMessage(
            999, BotUserId, "Games", 4, 5);
        var messageResult = await PreviewAsync(missingMessage);
        Assert.Null(messageResult.Draft);
        Assert.Contains("source message no longer exists", messageResult.Content,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePreviewAsync_RejectsMalformedBindingAndRoleCount()
    {
        var badBinding = CreateFixture(new ReactionRoleSettings(
            [new RoleEmotePair("4", "emoji")], "1", "invalid", "20"));
        var badBindingResult = await PreviewAsync(badBinding);
        Assert.Null(badBindingResult.Draft);
        Assert.Contains("identity is malformed", badBindingResult.Content,
            StringComparison.OrdinalIgnoreCase);

        var emptyRoles = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId));
        var emptyResult = await PreviewAsync(emptyRoles);
        Assert.Null(emptyResult.Draft);
        Assert.Contains("1–25 roles", emptyResult.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatePreviewAsync_RejectsLostTargetChannelPermission()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5));
        fixture.Bot.CanUseChannel = false;

        var result = await PreviewAsync(fixture);

        Assert.Null(result.Draft);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task ConfirmAsync_RejectsMissingSourceAndLostTargetPermission()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5));
        var draft = Assert.IsType<RoleMenuDraft>((await PreviewAsync(fixture)).Draft);

        fixture.ReactionStore.Settings = null;
        var missing = await fixture.Service.ConfirmAsync(draft,
            AdministratorId, BotUserId, CancellationToken.None);
        Assert.False(missing.Completed);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);

        fixture.ReactionStore.Settings = CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5);
        fixture.Bot.CanUseChannel = false;
        var denied = await fixture.Service.ConfirmAsync(draft,
            AdministratorId, BotUserId, CancellationToken.None);
        Assert.False(denied.Completed);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task ConfirmAsync_RejectsNonMigrationDraftAndIdentityCollision()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5));
        var draft = Assert.IsType<RoleMenuDraft>((await PreviewAsync(fixture)).Draft);
        var notMigration = await fixture.Service.ConfirmAsync(
            draft with { LegacyReactionRoleMessageId = null },
            AdministratorId, BotUserId, CancellationToken.None);
        Assert.False(notMigration.Completed);

        fixture.RoleMenuStore.Settings = new RoleMenuSettings(
            draft.MenuId, "1", "10", "99", "Other", "", ["4", "5"],
            RoleMenuSelectionMode.Multiple);
        var collision = await fixture.Service.ConfirmAsync(draft,
            AdministratorId, BotUserId, CancellationToken.None);
        Assert.False(collision.Completed);
        Assert.Contains("collision", collision.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task ConfirmAsync_PublishesOneNativeMenuAndLeavesLegacySourceUntouched()
    {
        var original = CreateLegacySettings(GuildId, ChannelId, LegacyMessageId, 4, 5);
        var fixture = CreateFixture(original);
        var sourceMessage = fixture.SourceChannel.Message;
        var draft = Assert.IsType<RoleMenuDraft>((await PreviewAsync(fixture)).Draft);
        fixture.SourceChannel.PublishedMenuId = draft.MenuId;

        var result = await fixture.Service.ConfirmAsync(draft,
            AdministratorId, BotUserId, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(RoleMenuPublicationStatus.Published, result.Publication?.Status);
        Assert.Contains("[View menu](https://discord.com/channels/",
            result.Content, StringComparison.Ordinal);
        Assert.Equal(1, fixture.SourceChannel.SentCount);
        Assert.Equal(1, fixture.RoleMenuStore.UpsertCalls);
        Assert.Equal("20", fixture.RoleMenuStore.Settings?.MigratedFromReactionRoleMessageId);
        Assert.Equal(["4", "5"], fixture.RoleMenuStore.Settings?.RoleIds);
        Assert.Equal(RoleMenuSelectionMode.Multiple,
            fixture.RoleMenuStore.Settings?.SelectionMode);
        Assert.Same(original, fixture.ReactionStore.Settings);
        Assert.Same(sourceMessage, fixture.SourceChannel.Message);

        var repeat = await fixture.Service.ConfirmAsync(draft,
            AdministratorId, BotUserId, CancellationToken.None);
        Assert.True(repeat.Completed);
        Assert.Contains("already migrated", repeat.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.SourceChannel.SentCount);
    }

    [Fact]
    public async Task MigratedMenu_AfterRepairStillResolvesToSameSourceWithoutAnotherPublication()
    {
        var fixture = CreateFixture(CreateLegacySettings(
            GuildId, ChannelId, LegacyMessageId, 4, 5));
        var migration = Assert.IsType<RoleMenuDraft>((await PreviewAsync(fixture)).Draft);
        fixture.SourceChannel.PublishedMenuId = migration.MenuId;
        var published = await fixture.Service.ConfirmAsync(
            migration, AdministratorId, BotUserId, CancellationToken.None);
        Assert.True(published.Completed);

        var saved = Assert.IsType<RoleMenuSettings>(fixture.RoleMenuStore.Settings);
        Assert.True(RoleMenuSettingsParser.TryParse(saved, out var parsed, out _));
        var repair = RoleMenuRepairWorkflow.CreateRepairDraft(
            saved, parsed, AdministratorId, ChannelId);
        fixture.RoleMenuStore.Settings = RoleMenuPublicationSettings.Create(
            repair, messageId: 88);

        var rerun = await PreviewAsync(fixture);

        Assert.Null(rerun.Draft);
        Assert.Same(fixture.RoleMenuStore.Settings, rerun.ExistingMenu);
        Assert.Contains("already migrated", rerun.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.SourceChannel.SentCount);
    }

    [Fact]
    public async Task ConfirmAsync_AmbiguousDiscordSendDoesNotRetryOrSaveMigration()
    {
        var original = CreateLegacySettings(GuildId, ChannelId, LegacyMessageId, 4, 5);
        var fixture = CreateFixture(original);
        var draft = Assert.IsType<RoleMenuDraft>((await PreviewAsync(fixture)).Draft);
        fixture.SourceChannel.PublishedMenuId = draft.MenuId;
        fixture.SourceChannel.FailSend = true;

        var result = await fixture.Service.ConfirmAsync(draft,
            AdministratorId, BotUserId, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(RoleMenuPublicationStatus.PanelOutcomeUnknown,
            result.Publication?.Status);
        Assert.Equal(1, fixture.SourceChannel.SentCount);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
        Assert.Same(original, fixture.ReactionStore.Settings);
    }

    [Fact]
    public async Task ConfirmAsync_StopsWhenTargetOrActorDisappears()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5));
        var draft = Assert.IsType<RoleMenuDraft>((await PreviewAsync(fixture)).Draft);

        fixture.SourceChannel.TargetAvailable = false;
        var targetGone = await fixture.Service.ConfirmAsync(draft,
            AdministratorId, BotUserId, CancellationToken.None);
        Assert.False(targetGone.Completed);
        Assert.Contains("target channel", targetGone.Content,
            StringComparison.OrdinalIgnoreCase);

        fixture.SourceChannel.TargetAvailable = true;
        fixture.SourceChannel.UsersAvailable = false;
        var actorGone = await fixture.Service.ConfirmAsync(draft,
            AdministratorId, BotUserId, CancellationToken.None);
        Assert.False(actorGone.Completed);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public async Task CreatePreviewAsync_DoesNotReplaceDraftAlreadyPublishing()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5));
        var draft = Assert.IsType<RoleMenuDraft>((await PreviewAsync(fixture)).Draft);
        Assert.Equal(RoleMenuDraftAccessStatus.Acquired,
            fixture.RoleMenus.TryBeginPublish(draft.Id, GuildId, AdministratorId, out _));

        var repeated = await PreviewAsync(fixture);

        Assert.Null(repeated.Draft);
        Assert.Contains("still publishing", repeated.Content,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePreviewAsync_ExistingMigrationWithBrokenLinkGivesAuditStep()
    {
        var fixture = CreateFixture(null);
        fixture.RoleMenuStore.Settings = new RoleMenuSettings(
            RoleMenuMigrationIdentity.CreateMenuId(GuildId, LegacyMessageId),
            "1", "invalid", "99", "Games", "", ["4"],
            RoleMenuSelectionMode.Multiple, "20");

        var result = await PreviewAsync(fixture);

        Assert.Null(result.Draft);
        Assert.Contains("/role-menu audit", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, fixture.ReactionStore.GetCalls);
    }

    [Fact]
    public async Task CreatePreviewAsync_UsesSharedBoundedDraftCapacity()
    {
        var fixture = CreateFixture(CreateLegacySettings(GuildId, ChannelId,
            LegacyMessageId, 4, 5), draftCapacity: 1);
        Assert.NotNull((await PreviewAsync(fixture)).Draft);

        var secondOwner = await fixture.Service.CreatePreviewAsync(
            new RoleMenuMigrationRequest(LegacyMessageId, null, null, null, null, null),
            GuildId, AdministratorId + 1, BotUserId, CancellationToken.None);

        Assert.Null(secondOwner.Draft);
        Assert.Contains("maximum number", secondOwner.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.RoleMenuStore.UpsertCalls);
    }

    [Fact]
    public void PublicationFailureCopy_ExplainsEachUncertainResult()
    {
        foreach (var status in new[]
                 {
                     RoleMenuPublicationStatus.PanelOutcomeUnknown,
                     RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed,
                     RoleMenuPublicationStatus.PersistenceOutcomeUnknown
                 })
        {
            var copy = RoleMenuMigrationService.FormatMigrationPublicationFailure(status);
            Assert.False(string.IsNullOrWhiteSpace(copy));
            Assert.DoesNotContain("MongoDB", copy, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LegacyClient_DistinguishesNotFoundAndForeignChannel()
    {
        var notFound = new HttpException(HttpStatusCode.NotFound, null);
        var channelGone = new LegacyReactionRoleMigrationClient(
            (_, _) => Task.FromException<IChannel?>(notFound));
        Assert.Equal(LegacyReactionRoleMigrationPanelLookupStatus.ChannelMissing,
            (await channelGone.ReadSourcePanelAsync(GuildId, ChannelId,
                LegacyMessageId, BotUserId, [4, 5], CancellationToken.None)).Status);

        var source = CreateTextChannel(GuildId, ChannelId,
            CreateLegacyMessage(LegacyMessageId, BotUserId, "Games", 4, 5));
        var client = new LegacyReactionRoleMigrationClient(
            (_, _) => Task.FromResult<IChannel?>(source.Channel));
        source.FailMessageRead = true;
        Assert.Equal(LegacyReactionRoleMigrationPanelLookupStatus.MessageMissing,
            (await client.ReadSourcePanelAsync(GuildId, ChannelId,
                LegacyMessageId, BotUserId, [4, 5], CancellationToken.None)).Status);
        source.FailMessageRead = false;
        source.GuildId = 999;
        Assert.Equal(LegacyReactionRoleMigrationPanelLookupStatus.Unrecognized,
            (await client.ReadSourcePanelAsync(GuildId, ChannelId,
                LegacyMessageId, BotUserId, [4, 5], CancellationToken.None)).Status);

        Assert.Throws<ArgumentNullException>(() =>
            new LegacyReactionRoleMigrationClient((DiscordSocketClient)null!));
    }

    private static Task<RoleMenuMigrationPreviewResult> PreviewAsync(
        MigrationFixture fixture, RoleMenuMigrationRequest? request = null)
        => fixture.Service.CreatePreviewAsync(
            request ?? new RoleMenuMigrationRequest(
                LegacyMessageId, null, null, null, null, null),
            GuildId, AdministratorId, BotUserId, CancellationToken.None);

    private static MigrationFixture CreateFixture(
        ReactionRoleSettings? settings, int draftCapacity = RoleMenuConstants.MaximumDrafts)
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
        var secondAdministrator = CreateGuildUser(AdministratorId + 1, guild, [1001]);
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
            new RoleMenuDraftRegistry(
                TimeProvider.System, draftCapacity, RoleMenuConstants.DraftLifetime),
            new RoleMenuMutationCoordinator(),
            executionContext);

        var discordUserReads = 0;
        var discordChannelReads = 0;
        var discord = new DiscordRoleMenuClient(
            (guildId, userId, _) =>
            {
                discordUserReads++;
                IGuildUser? user = guildId != GuildId || !sourceChannel.UsersAvailable
                    ? null
                    : userId switch
                    {
                        BotUserId => bot,
                        AdministratorId => administrator,
                        AdministratorId + 1 => secondAdministrator,
                        _ => null
                    };
                return Task.FromResult(user);
            },
            (channelId, _) =>
            {
                discordChannelReads++;
                IChannel? channel = sourceChannel.TargetAvailable && channelId == ChannelId
                    ? sourceChannel.Channel : null;
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
            roleMenus,
            reactionStore,
            roleMenuStore,
            sourceChannel,
            (GuildUserProxy)bot,
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
                roleId.ToString(CultureInfo.InvariantCulture),
                $"emoji-{index}"))],
            guildId.ToString(CultureInfo.InvariantCulture),
            channelId.ToString(CultureInfo.InvariantCulture),
            messageId.ToString(CultureInfo.InvariantCulture));

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
        RoleMenuInteractionService RoleMenus,
        TrackingReactionRoleStore ReactionStore,
        TrackingRoleMenuStore RoleMenuStore,
        TextChannelProxy SourceChannel,
        GuildUserProxy Bot,
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

        public Task<ReactionRoleSettings?> GetByBindingAsync(
            string guildId, string channelId, string messageId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Settings?.GuildId == guildId
                && Settings.ChannelId == channelId && Settings.MessageId == messageId
                ? Settings : null);
        }

        public Task<List<ReactionRoleSettings>> GetGuildPageAsync(
            string guildId, ObjectId? cursor, bool newer, int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<List<ReactionRoleSettings>>(Settings?.GuildId == guildId
                ? [Settings] : []);
        }

        public Task<bool> DeleteBindingAsync(
            ReactionRoleSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Migration must never delete its legacy source.");
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

        public Task<List<RoleMenuSettings>> GetByMessageAsync(
            string guildId, string channelId, string messageId, int maximumResults,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<List<RoleMenuSettings>>(Settings?.GuildId == guildId
                && Settings.ChannelId == channelId && Settings.MessageId == messageId
                ? [Settings] : []);
        }

        public Task<List<RoleMenuSettings>> GetPageAsync(
            string guildId, RoleMenuPageCursor? cursor, int maximumResults,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<List<RoleMenuSettings>>(Settings?.GuildId == guildId
                ? [Settings] : []);
        }

        public Task<bool> DeleteBindingAsync(
            RoleMenuSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Migration must not delete role menus.");
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
        public bool CanManageRoles { get; set; } = true;
        public bool CanUseChannel { get; set; } = true;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_Id" => Id,
                "get_Guild" => Guild,
                "get_RoleIds" => RoleIds,
                "get_GuildPermissions" => new GuildPermissions(
                    CanManageRoles ? ManageRoles : 0),
                nameof(IGuildUser.GetPermissions) => new ChannelPermissions(
                    CanUseChannel ? RequiredChannelPermissions : 0),
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    public class TextChannelProxy : DispatchProxy
    {
        public ITextChannel Channel { get; set; } = null!;
        public ulong GuildId { get; set; }
        public ulong Id { get; set; }
        public IMessage Message { get; set; } = null!;
        public ObjectId PublishedMenuId { get; set; }
        public int SentCount { get; private set; }
        public bool FailSend { get; set; }
        public bool TargetAvailable { get; set; } = true;
        public bool UsersAvailable { get; set; } = true;
        public bool FailMessageRead { get; set; }

        private static async IAsyncEnumerable<IReadOnlyCollection<IMessage>> EmptyMessages()
        {
            yield return [];
            await Task.CompletedTask;
        }

        private Task<IUserMessage> SendPanel()
        {
            SentCount++;
            if (FailSend)
            {
                throw new TimeoutException("Discord send outcome unknown");
            }
            var message = DispatchProxy.Create<IUserMessage, PublishedMessageProxy>();
            var proxy = (PublishedMessageProxy)message;
            proxy.Id = 900;
            proxy.Author = CreateUser(BotUserId);
            proxy.Components = RoleMenuComponents.BuildPublicComponents(PublishedMenuId).Components;
            return Task.FromResult(message);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_Id" => Id,
                "get_GuildId" => GuildId,
                "get_ChannelType" => ChannelType.Text,
                nameof(IMessageChannel.GetMessageAsync) => FailMessageRead
                    ? Task.FromException<IMessage?>(new HttpException(HttpStatusCode.NotFound, null))
                    : Task.FromResult<IMessage?>(
                        args is [ulong messageId, ..] && messageId == Message.Id ? Message : null),
                nameof(IMessageChannel.GetMessagesAsync) => EmptyMessages(),
                nameof(IMessageChannel.SendMessageAsync) => SendPanel(),
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

    public class PublishedMessageProxy : DispatchProxy
    {
        public ulong Id { get; set; }
        public IUser Author { get; set; } = null!;
        public IReadOnlyCollection<IMessageComponent> Components { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_Id" => Id,
                "get_Author" => Author,
                "get_Components" => Components,
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
