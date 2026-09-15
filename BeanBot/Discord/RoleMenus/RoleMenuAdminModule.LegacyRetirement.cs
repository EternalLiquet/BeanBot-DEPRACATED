using BeanBot.Discord.ReactionRoles;
using BeanBot.Logging;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.RoleMenus;

public sealed partial class RoleMenuAdminModule
{
    private ReactionRoleService? _legacyReactionRoles;

    public RoleMenuAdminModule(
        RoleMenuInteractionService roleMenuService,
        DiscordRoleMenuClient discord,
        RoleMenuAdministrationService administration,
        ReactionRoleService legacyReactionRoles,
        ILogger<RoleMenuAdminModule> logger)
        : this(roleMenuService, discord, administration, logger)
    {
        _legacyReactionRoles = legacyReactionRoles
            ?? throw new ArgumentNullException(nameof(legacyReactionRoles));
    }

    [SlashCommand(
        "retire-legacy",
        "Safely retire one saved legacy reaction-role panel.",
        runMode: RunMode.Sync)]
    public async Task RetireLegacyAsync(
        [Summary("legacy-message-id", "Message ID of the saved legacy reaction-role panel")]
        string legacyMessageId)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => DeferAsync(
                ephemeral: true,
                DiscordRoleMenuClient.CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                "Inspecting the legacy reaction-role panel…",
                operationToken),
            cancellation.Token);

        if (Context.Guild is null)
        {
            await ReplaceResponseAsync(
                "Legacy reaction-role panels can only be retired inside a server.",
                cancellation.Token);
            return;
        }

        if (!RoleMenuCustomIds.TryParseSnowflake(legacyMessageId?.Trim() ?? string.Empty, out var messageId))
        {
            await ReplaceResponseAsync(
                "That legacy message ID is invalid. Copy the numeric Discord message ID and try again.",
                cancellation.Token);
            return;
        }

        try
        {
            var preview = await CreateLegacyRetirementPreviewAsync(
                messageId,
                Context.Guild.Id,
                Context.Guild.CurrentUser.Id,
                cancellation.Token);
            if (preview is null)
            {
                return;
            }

            await ReplaceResponseAsync(
                "Review this destructive action before confirming.",
                cancellation.Token,
                LegacyReactionRoleRetirementComponents.BuildConfirmationEmbed(preview),
                LegacyReactionRoleRetirementComponents.BuildConfirmationComponents(
                    Context.User.Id,
                    messageId));
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !RoleMenus.IsShuttingDown)
        {
            await SendFreshFeedbackAsync(
                "Bean Bot ran out of time while inspecting that legacy panel. Nothing was changed; try again.");
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
                "Bean Bot could not safely inspect that legacy panel. Nothing was changed; try again after checking its channel access.");
        }
    }

    [ComponentInteraction(
        LegacyReactionRoleRetirementCustomIds.ConfirmPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ConfirmLegacyRetirementAsync(string userIdValue, string messageIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !RoleMenuCustomIds.TryParseSnowflake(messageIdValue, out var messageId)
            || !TryGetGuildActors(out var guild, out _, out var bot)
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidPrivateComponent(
                component,
                guild,
                ComponentType.Button,
                LegacyReactionRoleRetirementCustomIds.Confirm(boundUserId, messageId)))
        {
            await RespondToInvalidComponentAsync(
                "That legacy-retirement confirmation is invalid or belongs to another administrator.",
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
                        operationToken);
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
    public async Task CancelLegacyRetirementAsync(string userIdValue, string messageIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        var isOwner = RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            && boundUserId == Context.User.Id
            && RoleMenuCustomIds.TryParseSnowflake(messageIdValue, out var messageId)
            && Context.Guild is not null
            && Context.Interaction is SocketMessageComponent component
            && IsValidPrivateComponent(
                component,
                Context.Guild,
                ComponentType.Button,
                LegacyReactionRoleRetirementCustomIds.Cancel(boundUserId, messageId));
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
                "No saved legacy reaction-role configuration exists for that message ID in this server.",
                cancellationToken);
            return null;
        }

        if (!LegacyReactionRoleSourceParser.TryParse(settings, out var source)
            || source is null
            || source.GuildId != guildId
            || source.MessageId != messageId)
        {
            await ReplaceResponseAsync(
                "The saved legacy configuration does not match this server and message. Nothing can be retired safely.",
                cancellationToken);
            return null;
        }

        var client = new LegacyReactionRoleRetirementClient(Context.Client);
        var lookup = await client.ReadPanelAsync(source, botUserId, cancellationToken);
        if (lookup.Status is LegacyReactionRolePanelLookupStatus.UnexpectedChannel
            or LegacyReactionRolePanelLookupStatus.Unrecognized)
        {
            await ReplaceResponseAsync(
                "The saved message could not be positively identified as the expected Bean Bot legacy role panel. Nothing was changed.",
                cancellationToken);
            return null;
        }

        var mappings = source.RoleIds
            .Zip(
                settings.RoleEmotePairs,
                (roleId, pair) => new LegacyReactionRoleRetirementMapping(
                    roleId,
                    pair.EmojiId))
            .ToArray();
        return new LegacyReactionRoleRetirementPreview(
            source,
            lookup.SuggestedTitle,
            lookup.Status is LegacyReactionRolePanelLookupStatus.ChannelMissing
                or LegacyReactionRolePanelLookupStatus.MessageMissing,
            mappings);
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
