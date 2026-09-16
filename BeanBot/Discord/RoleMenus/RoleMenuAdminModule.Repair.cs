using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using MongoDB.Bson;

using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuPresentation;
using static BeanBot.Discord.RoleMenus.RoleMenuSetupValidation;

namespace BeanBot.Discord.RoleMenus;

public sealed partial class RoleMenuAdminModule
{
    [SlashCommand(
        "repair",
        "Restore a missing role-menu panel without losing its saved configuration.",
        runMode: RunMode.Sync)]
    public async Task RepairAsync(
        [Summary("menu-id", "ID shown in the role panel footer")]
        string menuId,
        [Summary("target-channel", "Replacement channel; required if the saved channel is gone")]
        ITextChannel? targetChannel = null)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => DeferAsync(
                ephemeral: true,
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                "Inspecting the saved role menu…",
                operationToken),
            cancellation.Token);

        if (!TryGetGuildActors(out var guild, out var administrator, out var bot))
        {
            await ReplaceResponseAsync(
                "Role menus can only be repaired inside a server.",
                cancellation.Token);
            return;
        }

        if (!RoleMenuCustomIds.TryParseMenuId(menuId.Trim(), out var parsedMenuId))
        {
            await ReplaceResponseAsync(
                "That menu ID is invalid. Copy the ID from the role panel footer or saved configuration.",
                cancellation.Token);
            return;
        }

        if (targetChannel is not null
            && (targetChannel.GuildId != guild.Id || targetChannel.ChannelType != ChannelType.Text))
        {
            await ReplaceResponseAsync(
                "Choose a normal text channel from this server as the replacement target.",
                cancellation.Token);
            return;
        }

        RoleMenuRepairInspectionResult inspection;
        try
        {
            inspection = await InspectRepairAsync(
                parsedMenuId,
                guild.Id,
                bot.Id,
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationFailed(_logger, parsedMenuId.ToString(), exception);
            await ReplaceResponseAsync(
                "Bean Bot couldn't confirm the saved panel state. No replacement was posted. Try again after Discord is reachable.",
                cancellation.Token);
            return;
        }

        if (!inspection.IsEligible)
        {
            await ReplaceResponseAsync(
                FormatRepairInspection(inspection),
                cancellation.Token);
            return;
        }

        var settings = inspection.Settings
            ?? throw new InvalidOperationException("An eligible repair did not return saved settings.");
        var parsed = inspection.ParsedSettings
            ?? throw new InvalidOperationException("An eligible repair did not return parsed settings.");
        if (inspection.PanelIssue == RoleMenuRepairPanelIssue.ChannelMissing
            && targetChannel is null)
        {
            await ReplaceResponseAsync(
                "The saved panel's channel no longer exists. Rerun `/role-menu repair` and choose a replacement `target-channel`.",
                cancellation.Token);
            return;
        }

        var targetChannelId = targetChannel?.Id ?? parsed.ChannelId;
        var requestOptions = CreateRequestOptions(cancellation.Token);
        var currentAdministrator = await _discord.GetGuildUserAsync(
            guild.Id,
            administrator.Id,
            requestOptions);
        var currentBot = await _discord.GetGuildUserAsync(guild.Id, bot.Id, requestOptions);
        if (currentAdministrator is null || currentBot is null)
        {
            await ReplaceResponseAsync(
                "Bean Bot couldn't refresh the current server role hierarchy. No replacement was posted.",
                cancellation.Token);
            return;
        }

        var validation = ValidateRoles(parsed.RoleIds, currentAdministrator, currentBot);
        if (!validation.IsValid)
        {
            await ReplaceResponseAsync(
                FormatRoleValidationFailure(validation),
                cancellation.Token);
            return;
        }

        var currentTarget = await _discord.GetGuildTextChannelAsync(
            guild.Id,
            targetChannelId,
            requestOptions);
        if (currentTarget is null)
        {
            await ReplaceResponseAsync(
                "The replacement target channel no longer exists or is not a normal text channel. Choose another target and retry.",
                cancellation.Token);
            return;
        }

        var channelPermissionFailure = GetChannelPermissionFailure(currentBot, currentTarget);
        if (channelPermissionFailure is not null)
        {
            await ReplaceResponseAsync(channelPermissionFailure, cancellation.Token);
            return;
        }

        await ReplaceResponseAsync(
            "The saved configuration is intact and the original panel is confirmed missing. Confirm the repair below.",
            cancellation.Token,
            RoleMenuRepairUi.BuildConfirmationEmbed(
                settings,
                validation.Roles,
                targetChannelId,
                inspection.PanelIssue),
            RoleMenuRepairUi.BuildConfirmationComponents(
                Context.User.Id,
                settings.Id,
                targetChannelId));
    }

    [ComponentInteraction(
        RoleMenuRepairUi.ConfirmPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ConfirmRepairAsync(
        string userIdValue,
        string menuIdValue,
        string targetChannelIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !RoleMenuCustomIds.TryParseMenuId(menuIdValue, out var menuId)
            || !RoleMenuCustomIds.TryParseSnowflake(targetChannelIdValue, out var targetChannelId)
            || !TryGetGuildActors(out var guild, out _, out var bot)
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidPrivateComponent(
                component,
                guild,
                ComponentType.Button,
                RoleMenuRepairUi.Confirm(boundUserId, menuId, targetChannelId)))
        {
            await RespondToInvalidComponentAsync(
                "That repair confirmation is invalid or belongs to another administrator.",
                cancellation.Token);
            return;
        }

        if (!await AcknowledgeEphemeralComponentAsync(
                "Repairing the role menu…",
                cancellation.Token))
        {
            return;
        }

        var mutationStarted = false;
        try
        {
            var feedback = await RoleMenus.RunMenuMutationAsync(
                menuId,
                operationToken =>
                {
                    mutationStarted = true;
                    return RepairUnderLockAsync(
                        menuId,
                        guild.Id,
                        Context.User.Id,
                        bot.Id,
                        targetChannelId,
                        operationToken);
                },
                cancellation.Token);
            await SendFreshFeedbackAsync(feedback);
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !RoleMenus.IsShuttingDown)
        {
            await SendFreshFeedbackAsync(
                mutationStarted
                    ? "Bean Bot ran out of time and couldn't confirm the final repair state. Inspect the target channel, then rerun the same repair; the stable menu ID is used to reconcile a prior replacement before posting another one."
                    : "Bean Bot was busy and did not begin this repair. Try again.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationFailed(_logger, menuId.ToString(), exception);
            await SendFreshFeedbackAsync(
                "Bean Bot couldn't confirm the repair result. The saved configuration was not intentionally deleted. Inspect the target channel and rerun the same repair to reconcile safely.");
        }
    }

    [ComponentInteraction(
        RoleMenuRepairUi.CancelPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task CancelRepairAsync(string userIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        var isOwner = RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            && boundUserId == Context.User.Id
            && Context.Guild is not null
            && Context.Interaction is SocketMessageComponent component
            && IsValidPrivateComponent(
                component,
                Context.Guild,
                ComponentType.Button,
                RoleMenuRepairUi.Cancel(boundUserId));
        if (!isOwner)
        {
            await RespondToInvalidComponentAsync(
                "That repair confirmation belongs to another administrator.",
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
                        "Role-menu repair cancelled.",
                        null,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    "Role-menu repair cancelled.",
                    operationToken),
                cancellation.Token);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Role-menu repair cancelled.",
            cancellation.Token);
    }

    private Task<RoleMenuRepairInspectionResult> InspectRepairAsync(
        ObjectId menuId,
        ulong guildId,
        ulong botUserId,
        CancellationToken cancellationToken)
        => RoleMenuRepairWorkflow.InspectAsync(
            menuId,
            guildId,
            botUserId,
            RoleMenus.GetAsync,
            _discord.ReadDeletionPanelAsync,
            cancellationToken);

    private async Task<string> RepairUnderLockAsync(
        ObjectId menuId,
        ulong guildId,
        ulong administratorId,
        ulong botUserId,
        ulong targetChannelId,
        CancellationToken cancellationToken)
    {
        var initialInspection = await InspectRepairAsync(
            menuId,
            guildId,
            botUserId,
            cancellationToken);
        if (!initialInspection.IsEligible)
        {
            return FormatRepairInspection(initialInspection);
        }

        var requestOptions = CreateRequestOptions(cancellationToken);
        var currentAdministrator = await _discord.GetGuildUserAsync(
            guildId,
            administratorId,
            requestOptions);
        var currentBot = await _discord.GetGuildUserAsync(
            guildId,
            botUserId,
            requestOptions);
        if (currentAdministrator is null || currentBot is null)
        {
            return "Bean Bot couldn't refresh the current server role hierarchy. No replacement was posted.";
        }

        var targetChannel = await _discord.GetGuildTextChannelAsync(
            guildId,
            targetChannelId,
            requestOptions);
        if (targetChannel is null)
        {
            return "The selected replacement channel no longer exists or is not a normal text channel. No replacement was posted.";
        }

        var finalInspection = await InspectRepairAsync(
            menuId,
            guildId,
            currentBot.Id,
            cancellationToken);
        if (!finalInspection.IsEligible)
        {
            return FormatRepairInspection(finalInspection);
        }

        var initialSettings = initialInspection.Settings
            ?? throw new InvalidOperationException("An eligible repair did not return saved settings.");
        var settings = finalInspection.Settings
            ?? throw new InvalidOperationException("An eligible repair did not return saved settings.");
        var parsed = finalInspection.ParsedSettings
            ?? throw new InvalidOperationException("An eligible repair did not return parsed settings.");
        if (!RoleMenuRepairWorkflow.HasSameSavedConfiguration(initialSettings, settings))
        {
            return "The saved role-menu configuration changed while this repair was being confirmed. No replacement was posted; rerun `/role-menu repair` to review the current state.";
        }

        var roleValidation = ValidateRoles(parsed.RoleIds, currentAdministrator, currentBot);
        if (!roleValidation.IsValid)
        {
            return FormatRoleValidationFailure(roleValidation);
        }

        var channelPermissionFailure = GetChannelPermissionFailure(currentBot, targetChannel);
        if (channelPermissionFailure is not null)
        {
            return channelPermissionFailure;
        }

        var draft = RoleMenuRepairWorkflow.CreateRepairDraft(
            settings,
            parsed,
            administratorId,
            targetChannelId);
        var publication = await _administration.PublishAsync(
            draft,
            targetChannel,
            currentBot.Id,
            cancellationToken);
        return FormatRepairPublication(publication, guildId, targetChannelId);
    }

    private static string FormatRepairInspection(RoleMenuRepairInspectionResult inspection)
        => inspection.Status switch
        {
            RoleMenuRepairInspectionStatus.SettingsMissing =>
                "No saved role menu with that ID exists in this server.",
            RoleMenuRepairInspectionStatus.SettingsInvalid =>
                "The saved role-menu configuration is malformed. Repair stopped without posting or changing anything; inspect or delete the stale configuration instead.",
            RoleMenuRepairInspectionStatus.Healthy =>
                "The saved role-menu panel is present and still matches Bean Bot's expected panel. Repair did nothing; this command will not move or duplicate a healthy panel.",
            RoleMenuRepairInspectionStatus.UnexpectedPanel =>
                "The saved message location is not definitely missing and no longer matches the expected Bean Bot panel. Repair refused to post a replacement to avoid creating a duplicate; inspect that message and saved binding first.",
            _ => "The saved panel is missing and can be repaired."
        };

    private static string FormatRepairPublication(
        RoleMenuPublicationResult result,
        ulong guildId,
        ulong targetChannelId)
    {
        if (result.Status == RoleMenuPublicationStatus.Published
            && result.MessageId is ulong messageId)
        {
            return "Role menu repaired without changing its stable menu ID or configured roles: " +
                   CreateMessageUrl(guildId, targetChannelId, messageId);
        }

        return result.Status switch
        {
            RoleMenuPublicationStatus.PanelOutcomeUnknown =>
                "Discord returned an ambiguous result while publishing the replacement. Bean Bot did not blindly retry. Inspect the target channel, then rerun the same repair so the stable menu ID can reconcile any panel that was actually created.",
            RoleMenuPublicationStatus.PersistenceOutcomeUnknown =>
                "A replacement panel was found or created, but Bean Bot could not confirm the MongoDB binding update. The saved configuration was not intentionally deleted. Rerun the same repair with this target to reconcile the stable menu ID without blindly posting another panel.",
            RoleMenuPublicationStatus.PersistenceAbsentPanelRolledBack =>
                "The saved configuration disappeared during repair, so Bean Bot removed the replacement panel. No replacement remains; inspect the saved state before retrying.",
            RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed =>
                "The saved configuration disappeared during repair and Bean Bot could not remove the replacement panel. Inspect the target channel and persistence before retrying to avoid a duplicate.",
            _ =>
                "Bean Bot could not confirm the repair result. Inspect the target channel and saved configuration before retrying."
        };
    }
}
