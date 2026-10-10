using System.Globalization;
using BeanBot.Discord.ReactionRoles;
using BeanBot.Logging;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace BeanBot.Discord.RoleMenus;

public sealed partial class RoleMenuAdminModule
{
    private ReactionRoleService? _legacyReactionRoles;

    public RoleMenuAdminModule(
        RoleMenuInteractionService roleMenuService,
        DiscordRoleMenuClient discord,
        RoleMenuAdministrationService administration,
        RoleMenuAuditService audit,
        ReactionRoleService legacyReactionRoles,
        ILogger<RoleMenuAdminModule> logger)
        : this(roleMenuService, discord, administration, audit, logger)
    {
        _legacyReactionRoles = legacyReactionRoles
            ?? throw new ArgumentNullException(nameof(legacyReactionRoles));
    }

    [SlashCommand(
        "retire-legacy",
        "Retire a legacy reaction-role panel. Members keep their roles.",
        runMode: RunMode.Sync)]
    public async Task RetireLegacyAsync()
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => DeferAsync(ephemeral: true,
                DiscordRoleMenuClient.CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync("Loading legacy panels…", operationToken),
            cancellation.Token);
        if (Context.Guild is null)
        {
            await ReplaceResponseAsync("You can only retire legacy panels in a server.",
                cancellation.Token);
            return;
        }
        await ShowLegacyPageAsync(null, newer: false, cancellation.Token);
    }

    [ComponentInteraction(LegacyReactionRoleRetirementPicker.PagePattern,
        ignoreGroupNames: true, runMode: RunMode.Sync)]
    public async Task ChangeLegacyPageAsync(
        string userIdValue, string directionValue, string cursorValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var userId)
            || userId != Context.User.Id
            || !LegacyReactionRoleRetirementPicker.TryParseCursor(
                directionValue, cursorValue, out var newer, out var cursor)
            || Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(component, Context.Guild,
                ComponentType.Button,
                LegacyReactionRoleRetirementPicker.Page(userId, newer, cursor)))
        {
            await RespondToInvalidComponentAsync(
                "This list has expired or belongs to someone else. Run `/role-menu retire-legacy` again.",
                cancellation.Token);
            return;
        }
        if (!await AcknowledgeEphemeralComponentAsync("Loading legacy panels…",
                cancellation.Token))
        {
            return;
        }
        await ShowLegacyPageAsync(cursor, newer, cancellation.Token);
    }

    [ComponentInteraction(LegacyReactionRoleRetirementPicker.SelectPattern,
        ignoreGroupNames: true, runMode: RunMode.Sync)]
    public async Task SelectLegacyPanelAsync(string userIdValue, string[] selectedMessageIds)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var userId)
            || userId != Context.User.Id
            || selectedMessageIds is not { Length: 1 }
            || !RoleMenuCustomIds.TryParseSnowflake(selectedMessageIds[0], out var messageId)
            || !TryGetGuildActors(out var guild, out _, out var bot)
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(component, guild,
                ComponentType.SelectMenu,
                LegacyReactionRoleRetirementPicker.Select(userId), selectedMessageIds[0]))
        {
            await RespondToInvalidComponentAsync(
                "This list has expired or belongs to someone else. Run `/role-menu retire-legacy` again.",
                cancellation.Token);
            return;
        }
        if (!await AcknowledgeEphemeralComponentAsync("Checking that panel…",
                cancellation.Token))
        {
            return;
        }
        try
        {
            var preview = await CreateLegacyRetirementPreviewAsync(messageId, guild.Id,
                bot.Id, cancellation.Token);
            if (preview is null)
            {
                return;
            }
            var expiry = DateTimeOffset.UtcNow.Add(
                LegacyReactionRoleRetirementCustomIds.Lifetime).ToUnixTimeSeconds();
            await ReplaceResponseAsync("Delete this legacy role panel?", cancellation.Token,
                LegacyReactionRoleRetirementComponents.BuildConfirmationEmbed(preview),
                LegacyReactionRoleRetirementComponents.BuildConfirmationComponents(
                    Context.User.Id, messageId, expiry, preview.Fingerprint));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested &&
                                                 !RoleMenus.IsShuttingDown)
        {
            await SendFreshFeedbackAsync("I ran out of time checking that panel. Run `/role-menu retire-legacy` again.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuDeletionFailed(_logger, messageId.ToString(CultureInfo.InvariantCulture), exception);
            await SendFreshFeedbackAsync("I couldn't inspect that panel. Check its channel access and run `/role-menu retire-legacy` again.");
        }
    }

    private async Task ShowLegacyPageAsync(ObjectId? cursor, bool newer,
        CancellationToken cancellationToken)
    {
        var guild = Context.Guild!;
        var settings = await LegacyReactionRoles.GetGuildPageAsync(
            guild.Id, cursor, newer, LegacyReactionRoleRetirementPicker.PageSize + 1,
            cancellationToken);
        await ReplaceResponseAsync(
            settings.Count == 0 ? "I couldn't find any saved legacy panels on this page."
                : "Which legacy role panel do you want to retire?",
            cancellationToken,
            components: LegacyReactionRoleRetirementPicker.Build(
                Context.User.Id, settings, cursor, newer,
                channelId => guild.GetChannel(channelId)?.Name,
                roleId => guild.GetRole(roleId)?.Name));
    }

    [ComponentInteraction(
        LegacyReactionRoleRetirementCustomIds.ConfirmPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ConfirmLegacyRetirementAsync(
        string userIdValue, string messageIdValue, string expiryValue, string fingerprint)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !RoleMenuCustomIds.TryParseSnowflake(messageIdValue, out var messageId)
            || !long.TryParse(expiryValue, out var expiresUnixSeconds)
            || !LegacyReactionRoleRetirementCustomIds.IsCurrent(
                expiresUnixSeconds, DateTimeOffset.UtcNow)
            || fingerprint.Length != 16
            || !TryGetGuildActors(out var guild, out _, out var bot)
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(
                component, guild, ComponentType.Button,
                LegacyReactionRoleRetirementCustomIds.Confirm(
                    boundUserId, messageId, expiresUnixSeconds, fingerprint)))
        {
            await RespondToInvalidComponentAsync(
                "This confirmation has expired or belongs to someone else. Run `/role-menu retire-legacy` again.",
                cancellation.Token);
            return;
        }

        if (!await AcknowledgeEphemeralComponentAsync(
                "Retiring the legacy reaction-role panel…",
                cancellation.Token))
        {
            return;
        }

        var client = new LegacyReactionRoleRetirementClient(Context.Client);
        var mutationStarted = false;
        try
        {
            var result = await LegacyReactionRoles.RunSettingsRetirementAsync(
                messageId,
                operationToken =>
                {
                    mutationStarted = true;
                    return LegacyReactionRoleRetirementWorkflow.ExecuteAsync(
                        messageId,
                        guild.Id,
                        CreateLegacyRetirementOperations(
                            client,
                            Context.User.Id,
                            bot.Id),
                        operationToken,
                        fingerprint);
                },
                cancellation.Token);
            LogLegacyRetirementFailures(messageId, result);
            await SendFreshFeedbackAsync(
                LegacyReactionRoleRetirementComponents.FormatResult(result));
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !RoleMenus.IsShuttingDown)
        {
            await SendFreshFeedbackAsync(
                mutationStarted
                    ? "Bean Bot ran out of time and could not confirm the final retirement state. No automatic retry was attempted. Inspect the legacy panel and rerun this command to reconcile it safely."
                    : "Bean Bot was busy and did not begin retiring that legacy panel. Try again.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuDeletionFailed(
                _logger,
                messageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                exception);
            await SendFreshFeedbackAsync(
                "Bean Bot could not confirm the retirement result. Inspect the legacy panel and rerun the command to reconcile the exact saved record safely.");
        }
    }

    [ComponentInteraction(
        LegacyReactionRoleRetirementCustomIds.CancelPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task CancelLegacyRetirementAsync(
        string userIdValue, string messageIdValue, string expiryValue, string fingerprint)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        var isOwner = RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            && boundUserId == Context.User.Id
            && RoleMenuCustomIds.TryParseSnowflake(messageIdValue, out var messageId)
            && long.TryParse(expiryValue, out var expiresUnixSeconds)
            && LegacyReactionRoleRetirementCustomIds.IsCurrent(
                expiresUnixSeconds, DateTimeOffset.UtcNow)
            && fingerprint.Length == 16
            && Context.Guild is not null
            && Context.Interaction is SocketMessageComponent component
            && IsValidManagementComponent(
                component, Context.Guild, ComponentType.Button,
                LegacyReactionRoleRetirementCustomIds.Cancel(
                    boundUserId, messageId, expiresUnixSeconds, fingerprint));
        if (!isOwner)
        {
            await RespondToInvalidComponentAsync(
                "That legacy-retirement confirmation belongs to another administrator.",
                cancellation.Token);
            return;
        }

        if (Context.Interaction is SocketMessageComponent validComponent)
        {
            await RoleMenus.ExecuteInitialResponseAsync(
                supportsOriginalResponse: true,
                operationToken => validComponent.UpdateAsync(
                    properties => SetMessage(
                        properties,
                        "Legacy reaction-role retirement cancelled. Nothing was changed.",
                        null,
                        MessageComponent.Empty),
                    DiscordRoleMenuClient.CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    "Legacy reaction-role retirement cancelled. Nothing was changed.",
                    operationToken),
                cancellation.Token);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Legacy reaction-role retirement cancelled. Nothing was changed.",
            cancellation.Token);
    }

    private ReactionRoleService LegacyReactionRoles
        => _legacyReactionRoles
           ?? throw new InvalidOperationException(
               "Legacy reaction-role retirement dependencies were not configured.");

    private async Task<LegacyReactionRoleRetirementPreview?> CreateLegacyRetirementPreviewAsync(
        ulong messageId,
        ulong guildId,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        var settings = await LegacyReactionRoles.GetFreshRoleSettingAsync(
            messageId,
            cancellationToken);
        if (settings is null)
        {
            await ReplaceResponseAsync(
                "I couldn't find that saved legacy panel. Run `/role-menu retire-legacy` again to choose another.",
                cancellationToken);
            return null;
        }

        if (!LegacyReactionRoleSourceParser.TryParse(settings, out var source)
            || source is null
            || source.GuildId != guildId
            || source.MessageId != messageId)
        {
            await ReplaceResponseAsync(
                "That saved panel no longer matches this server. Run `/role-menu retire-legacy` again.",
                cancellationToken);
            return null;
        }

        var client = new LegacyReactionRoleRetirementClient(Context.Client);
        var lookup = await client.ReadPanelAsync(source, botUserId, cancellationToken);
        if (lookup.Status is LegacyReactionRolePanelLookupStatus.UnexpectedChannel
            or LegacyReactionRolePanelLookupStatus.Unrecognized)
        {
            await ReplaceResponseAsync(
                "I couldn't verify that this is my legacy role panel. Check the message and run `/role-menu retire-legacy` again.",
                cancellationToken);
            return null;
        }

        var mappings = source.RoleIds
            .Zip(
                settings.RoleEmotePairs,
                (roleId, pair) => new LegacyReactionRoleRetirementMapping(
                    roleId, pair.EmojiId,
                    ulong.TryParse(pair.EmojiId, out var emojiId)
                        ? Context.Guild?.Emotes.FirstOrDefault(emoji => emoji.Id == emojiId)?.Name
                        : null,
                    Context.Guild?.GetRole(roleId)?.Name))
            .ToArray();
        return new LegacyReactionRoleRetirementPreview(
            source,
            lookup.SuggestedTitle,
            lookup.Status is LegacyReactionRolePanelLookupStatus.ChannelMissing
                or LegacyReactionRolePanelLookupStatus.MessageMissing,
            mappings,
            LegacyReactionRoleRetirementBinding.Fingerprint(settings));
    }

    private LegacyReactionRoleRetirementOperations CreateLegacyRetirementOperations(
        LegacyReactionRoleRetirementClient client,
        ulong administratorId,
        ulong botUserId)
        => new(
            LegacyReactionRoles.GetFreshRoleSettingAsync,
            cancellationToken => client.CanAdministratorManageRolesAsync(
                Context.Guild!.Id,
                administratorId,
                cancellationToken),
            (source, cancellationToken) => client.ReadPanelAsync(
                source,
                botUserId,
                cancellationToken),
            (panel, roleIds, cancellationToken) => client.DeletePanelAsync(
                panel,
                botUserId,
                roleIds,
                cancellationToken),
            LegacyReactionRoles.DeleteRoleSettingAsync,
            () => LegacyReactionRoles.IsShuttingDown);

    private void LogLegacyRetirementFailures(
        ulong messageId,
        LegacyReactionRoleRetirementResult result)
    {
        if (result.Failure is not null)
        {
            BeanBotLog.RoleMenuDeletionFailed(
                _logger,
                messageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result.Failure);
        }

        if (result.ReconciliationFailure is not null)
        {
            BeanBotLog.RoleMenuDeletionFailed(
                _logger,
                messageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result.ReconciliationFailure);
        }
    }
}
