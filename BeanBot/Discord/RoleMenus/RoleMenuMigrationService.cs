using System.Globalization;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuPresentation;
using static BeanBot.Discord.RoleMenus.RoleMenuSetupValidation;

namespace BeanBot.Discord.RoleMenus;

internal sealed record RoleMenuMigrationRequest(
    ulong LegacyMessageId,
    ulong? TargetChannelId,
    ulong? TargetChannelGuildId,
    ChannelType? TargetChannelType,
    string? Title,
    string? Description);

internal sealed record RoleMenuMigrationPreviewResult(
    string Content,
    RoleMenuDraft? Draft = null,
    IReadOnlyCollection<RoleMenuRoleSnapshot>? Roles = null,
    string? SourceMessageLink = null,
    RoleMenuSettings? ExistingMenu = null);

internal sealed record RoleMenuMigrationConfirmationResult(
    string Content,
    bool Completed,
    RoleMenuPublicationResult? Publication = null);

public sealed class RoleMenuMigrationService
{
    private readonly ReactionRoleRepository _reactionRoles;
    private readonly RoleMenuInteractionService _roleMenus;
    private readonly DiscordRoleMenuClient _discord;
    private readonly LegacyReactionRoleMigrationClient _legacyDiscord;
    private readonly RoleMenuAdministrationService _administration;

    public RoleMenuMigrationService(
        ReactionRoleRepository reactionRoles,
        RoleMenuInteractionService roleMenus,
        DiscordRoleMenuClient discord,
        LegacyReactionRoleMigrationClient legacyDiscord,
        RoleMenuAdministrationService administration)
    {
        _reactionRoles = reactionRoles ?? throw new ArgumentNullException(nameof(reactionRoles));
        _roleMenus = roleMenus ?? throw new ArgumentNullException(nameof(roleMenus));
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _legacyDiscord = legacyDiscord ?? throw new ArgumentNullException(nameof(legacyDiscord));
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));
    }

    internal async Task<RoleMenuMigrationPreviewResult> CreatePreviewAsync(
        RoleMenuMigrationRequest request,
        ulong guildId,
        ulong administratorId,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var menuId = RoleMenuMigrationIdentity.CreateMenuId(guildId, request.LegacyMessageId);
        var existing = await _roleMenus.GetAsync(menuId, guildId, cancellationToken);
        if (existing is not null)
        {
            return CreateExistingResult(existing, request.LegacyMessageId, guildId);
        }

        var validation = await LoadValidatedSourceAsync(
            request.LegacyMessageId,
            guildId,
            administratorId,
            botUserId,
            cancellationToken);
        if (!validation.IsValid)
        {
            return new RoleMenuMigrationPreviewResult(validation.ErrorMessage);
        }

        var targetChannelId = request.TargetChannelId ?? validation.SourceChannelId;
        if (request.TargetChannelId is not null
            && (request.TargetChannelGuildId != guildId
                || request.TargetChannelType != ChannelType.Text))
        {
            return new RoleMenuMigrationPreviewResult(
                "Choose a normal text channel from this server as the migration target.");
        }

        var targetChannel = await _discord.GetGuildTextChannelAsync(
            guildId,
            targetChannelId,
            CreateRequestOptions(cancellationToken));
        if (targetChannel is null)
        {
            return new RoleMenuMigrationPreviewResult(
                "The target channel no longer exists or is not a normal text channel in this server.");
        }

        var permissionFailure = GetChannelPermissionFailure(validation.Bot!, targetChannel);
        if (permissionFailure is not null)
        {
            return new RoleMenuMigrationPreviewResult(permissionFailure);
        }

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = validation.SuggestedTitle;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return new RoleMenuMigrationPreviewResult(
                "Bean Bot recognized the legacy panel, but it has no usable `Role Group:` label. " +
                "Run the command again with an explicit `title` before migrating it.");
        }

        if (title.Length > RoleMenuConstants.MaximumTitleLength)
        {
            return new RoleMenuMigrationPreviewResult(
                $"The panel title must be 1–{RoleMenuConstants.MaximumTitleLength} characters.");
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length > RoleMenuConstants.MaximumDescriptionLength)
        {
            return new RoleMenuMigrationPreviewResult(
                $"The description cannot exceed {RoleMenuConstants.MaximumDescriptionLength} characters.");
        }

        var createStatus = _roleMenus.CreateMigrationDraft(
            guildId,
            administratorId,
            targetChannelId,
            title,
            description,
            validation.RoleIds!,
            menuId,
            request.LegacyMessageId,
            out var draft);
        if (createStatus != RoleMenuDraftCreateStatus.Created || draft is null)
        {
            return new RoleMenuMigrationPreviewResult(createStatus == RoleMenuDraftCreateStatus.AlreadyPublishing
                ? "Your previous role-menu preview is still publishing. Wait for it to finish before starting a migration."
                : "Bean Bot is already holding the maximum number of role-menu previews. Try again after another preview expires.");
        }

        return new RoleMenuMigrationPreviewResult(
            "Review this private migration preview. The legacy reaction-role message and its saved " +
            "configuration will stay unchanged after publication until you retire them manually.",
            draft,
            validation.RoleValidation!.Roles,
            RoleMenuMigrationIdentity.BuildMessageLink(
                guildId,
                validation.SourceChannelId,
                request.LegacyMessageId));
    }

    internal async Task<RoleMenuMigrationConfirmationResult> ConfirmAsync(
        RoleMenuDraft draft,
        ulong administratorId,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.LegacyReactionRoleMessageId is not { } legacyMessageId)
        {
            return new RoleMenuMigrationConfirmationResult(
                "That preview is not a legacy reaction-role migration.",
                Completed: false);
        }

        var existing = await _roleMenus.GetAsync(draft.MenuId, draft.GuildId, cancellationToken);
        if (existing is not null)
        {
            var existingResult = CreateExistingResult(existing, legacyMessageId, draft.GuildId);
            return new RoleMenuMigrationConfirmationResult(
                existingResult.Content,
                Completed: IsExpectedMigration(existing, legacyMessageId));
        }

        var validation = await LoadValidatedSourceAsync(
            legacyMessageId,
            draft.GuildId,
            administratorId,
            botUserId,
            cancellationToken);
        if (!validation.IsValid)
        {
            return new RoleMenuMigrationConfirmationResult(
                "Migration stopped during the final safety check: " + validation.ErrorMessage,
                Completed: false);
        }

        if (!validation.RoleIds!.SequenceEqual(draft.RoleIds))
        {
            return new RoleMenuMigrationConfirmationResult(
                "The legacy role configuration changed after this preview was created. Nothing was " +
                "published; run `/role-menu migrate` again to review the current roles.",
                Completed: false);
        }

        var targetChannel = await _discord.GetGuildTextChannelAsync(
            draft.GuildId,
            draft.TargetChannelId,
            CreateRequestOptions(cancellationToken));
        if (targetChannel is null)
        {
            return new RoleMenuMigrationConfirmationResult(
                "The target channel no longer exists or is no longer a normal text channel in this server.",
                Completed: false);
        }

        var permissionFailure = GetChannelPermissionFailure(validation.Bot!, targetChannel);
        if (permissionFailure is not null)
        {
            return new RoleMenuMigrationConfirmationResult(permissionFailure, Completed: false);
        }

        var publication = await _administration.PublishAsync(
            draft,
            targetChannel,
            botUserId,
            cancellationToken);
        if (publication.Status == RoleMenuPublicationStatus.Published
            && publication.MessageId is { } publishedMessageId)
        {
            var link = RoleMenuMigrationIdentity.BuildMessageLink(
                draft.GuildId,
                draft.TargetChannelId,
                publishedMessageId);
            return new RoleMenuMigrationConfirmationResult(
                $"Migration published as role menu `{draft.MenuId}`: {link}\n" +
                "The legacy reaction-role panel and configuration were left unchanged. After you " +
                "verify the new menu, retire the legacy panel deliberately to avoid offering two active controls.",
                Completed: true,
                publication);
        }

        var failure = publication.CanRetry
            ? "Bean Bot confirmed that the attempted role-menu panel was rolled back and no migration was saved. " +
              "Run `/role-menu migrate` again after correcting the underlying problem."
            : FormatMigrationPublicationFailure(publication.Status);
        return new RoleMenuMigrationConfirmationResult(
            failure,
            Completed: publication.IsTerminal,
            publication);
    }

    private async Task<ValidatedLegacySource> LoadValidatedSourceAsync(
        ulong legacyMessageId,
        ulong guildId,
        ulong administratorId,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        var settings = await _reactionRoles.GetRoleSetting(legacyMessageId, cancellationToken);
        if (settings is null)
        {
            return ValidatedLegacySource.Invalid(
                "No saved legacy reaction-role configuration exists for that message ID.");
        }

        if (!RoleMenuCustomIds.TryParseSnowflake(settings.GuildId, out var sourceGuildId)
            || sourceGuildId != guildId)
        {
            return ValidatedLegacySource.Invalid(
                "That legacy reaction-role configuration belongs to a different server or is malformed.");
        }

        if (!RoleMenuCustomIds.TryParseSnowflake(settings.ChannelId, out var sourceChannelId)
            || !RoleMenuCustomIds.TryParseSnowflake(settings.MessageId, out var persistedMessageId)
            || persistedMessageId != legacyMessageId)
        {
            return ValidatedLegacySource.Invalid(
                "The saved legacy reaction-role channel/message identity is malformed. Nothing was migrated.");
        }

        if (!TryParseLegacyRoleIds(settings, out var roleIds, out var roleParseError))
        {
            return ValidatedLegacySource.Invalid(roleParseError);
        }

        var requestOptions = CreateRequestOptions(cancellationToken);
        var administrator = await _discord.GetGuildUserAsync(
            guildId,
            administratorId,
            requestOptions);
        var bot = await _discord.GetGuildUserAsync(guildId, botUserId, requestOptions);
        if (administrator is null || bot is null)
        {
            return ValidatedLegacySource.Invalid(
                "Bean Bot couldn't refresh the current administrator/bot role hierarchy. Try again in a moment.");
        }

        var roleValidation = ValidateRoles(roleIds, administrator, bot);
        if (!roleValidation.IsValid)
        {
            return ValidatedLegacySource.Invalid(FormatRoleValidationFailure(roleValidation));
        }

        var panel = await _legacyDiscord.ReadSourcePanelAsync(
            guildId,
            sourceChannelId,
            legacyMessageId,
            botUserId,
            roleIds,
            cancellationToken);
        var panelError = panel.Status switch
        {
            LegacyReactionRolePanelLookupStatus.Found => null,
            LegacyReactionRolePanelLookupStatus.ChannelMissing =>
                "The saved legacy source channel no longer exists. The source record was left unchanged.",
            LegacyReactionRolePanelLookupStatus.MessageMissing =>
                "The saved legacy source message no longer exists. The source record was left unchanged.",
            _ =>
                "The saved message does not positively match Bean Bot's legacy `Role Group:` panel shape. " +
                "Nothing was migrated and no title was inferred."
        };
        return panelError is not null
            ? ValidatedLegacySource.Invalid(panelError)
            : ValidatedLegacySource.Valid(
                sourceChannelId,
                roleIds,
                roleValidation,
                bot,
                panel.SuggestedTitle);
    }

    private static bool TryParseLegacyRoleIds(
        ReactionRoleSettings settings,
        out IReadOnlyList<ulong> roleIds,
        out string errorMessage)
    {
        var parsed = new List<ulong>(settings.RoleEmotePairs.Count);
        foreach (var pair in settings.RoleEmotePairs)
        {
            if (!RoleMenuCustomIds.TryParseSnowflake(pair.RoleId, out var roleId))
            {
                roleIds = [];
                errorMessage =
                    "The legacy configuration contains a malformed role ID. Nothing was migrated.";
                return false;
            }

            parsed.Add(roleId);
        }

        if (parsed.Count is < 1 or > RoleMenuConstants.MaximumRoles)
        {
            roleIds = [];
            errorMessage =
                $"The legacy configuration must contain 1–{RoleMenuConstants.MaximumRoles} roles before it can be migrated.";
            return false;
        }

        roleIds = parsed;
        errorMessage = string.Empty;
        return true;
    }

    private static RoleMenuMigrationPreviewResult CreateExistingResult(
        RoleMenuSettings existing,
        ulong legacyMessageId,
        ulong guildId)
    {
        if (!IsExpectedMigration(existing, legacyMessageId))
        {
            return new RoleMenuMigrationPreviewResult(
                "Bean Bot found a role-menu ID collision for this legacy source. No migration was attempted; " +
                "inspect the saved role-menu configuration before retrying.",
                ExistingMenu: existing);
        }

        var link = RoleMenuCustomIds.TryParseSnowflake(existing.ChannelId, out var channelId)
                   && RoleMenuCustomIds.TryParseSnowflake(existing.MessageId, out var messageId)
            ? RoleMenuMigrationIdentity.BuildMessageLink(guildId, channelId, messageId)
            : null;
        var suffix = link is null
            ? "Its saved channel/message identity is malformed, so Bean Bot cannot build a message link."
            : link;
        return new RoleMenuMigrationPreviewResult(
            $"That legacy panel was already migrated as role menu `{existing.Id}`. {suffix}",
            ExistingMenu: existing);
    }

    private static bool IsExpectedMigration(RoleMenuSettings settings, ulong legacyMessageId)
        => string.Equals(
            settings.MigratedFromReactionRoleMessageId,
            legacyMessageId.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    private static string FormatMigrationPublicationFailure(RoleMenuPublicationStatus status)
        => status switch
        {
            RoleMenuPublicationStatus.PanelOutcomeUnknown =>
                "Discord reported an error while publishing, and Bean Bot could not confirm whether " +
                "the migrated panel was created. Automatic retry is disabled. Inspect the target " +
                "channel and saved role-menu state before trying this migration again.",
            RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed =>
                "Bean Bot confirmed the migration settings were not saved but could not remove the " +
                "new panel. Automatic retry is disabled; remove the orphaned panel before retrying.",
            _ =>
                "Bean Bot could not confirm whether MongoDB saved the migrated role menu. The panel " +
                "was left in place and automatic retry is disabled. Inspect the target channel and " +
                "saved role-menu state before retrying."
        };

    private sealed record ValidatedLegacySource(
        bool IsValid,
        string ErrorMessage,
        ulong SourceChannelId,
        IReadOnlyList<ulong>? RoleIds,
        RoleMenuRoleValidationResult? RoleValidation,
        IGuildUser? Bot,
        string? SuggestedTitle)
    {
        internal static ValidatedLegacySource Invalid(string errorMessage)
            => new(false, errorMessage, 0, null, null, null, null);

        internal static ValidatedLegacySource Valid(
            ulong sourceChannelId,
            IReadOnlyList<ulong> roleIds,
            RoleMenuRoleValidationResult roleValidation,
            IGuildUser bot,
            string? suggestedTitle)
            => new(
                true,
                string.Empty,
                sourceChannelId,
                roleIds,
                roleValidation,
                bot,
                suggestedTitle);
    }
}
