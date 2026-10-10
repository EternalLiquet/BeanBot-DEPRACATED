using BeanBot.Logging;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
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
        "Restore a missing role menu message with its saved roles.",
        runMode: RunMode.Sync)]
    public async Task RepairAsync(
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
                "Checking the role menu…",
                operationToken),
            cancellation.Token);

        if (!TryGetGuildActors(out var guild, out var administrator, out var bot))
        {
            await ReplaceResponseAsync(
                "I can repair role menus only inside a server.",
                cancellation.Token);
            return;
        }

        if (targetChannel is not null
            && (targetChannel.GuildId != guild.Id || targetChannel.ChannelType != ChannelType.Text))
        {
            await ReplaceResponseAsync(
                "Choose a text channel in this server and run `/role-menu repair` again.",
                cancellation.Token);
            return;
        }

        await ShowRepairPageAsync(guild, targetChannel?.Id ?? 0, null, cancellation.Token);
    }

    [ComponentInteraction(RoleMenuRepairUi.PagePattern, ignoreGroupNames: true, runMode: RunMode.Sync)]
    public async Task ChangeRepairPageAsync(
        string userIdValue,
        string targetChannelIdValue,
        string directionValue,
        string createdAtTicksValue,
        string menuIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !ulong.TryParse(targetChannelIdValue, out var targetChannelId)
            || !RoleMenuCustomIds.TryParsePageCursor(
                directionValue, createdAtTicksValue, menuIdValue, out var cursor)
            || Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(
                component, Context.Guild, ComponentType.Button,
                RoleMenuRepairUi.Page(boundUserId, targetChannelId, cursor)))
        {
            await RespondToInvalidComponentAsync(
                "This list has expired or belongs to someone else. Run `/role-menu repair` again.",
                cancellation.Token);
            return;
        }

        if (await AcknowledgeEphemeralComponentAsync("Loading role menus…", cancellation.Token))
        {
            await ShowRepairPageAsync(Context.Guild, targetChannelId, cursor, cancellation.Token);
        }
    }

    [ComponentInteraction(RoleMenuRepairUi.SelectPattern, ignoreGroupNames: true, runMode: RunMode.Sync)]
    public async Task SelectRepairAsync(
        string userIdValue,
        string targetChannelIdValue,
        string[] selectedMenuIds)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !ulong.TryParse(targetChannelIdValue, out var targetChannelId)
            || selectedMenuIds is not { Length: 1 }
            || !RoleMenuCustomIds.TryParseMenuId(selectedMenuIds[0], out var menuId)
            || !TryGetGuildActors(out var guild, out var administrator, out var bot)
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(
                component, guild, ComponentType.SelectMenu,
                RoleMenuRepairUi.Select(boundUserId, targetChannelId), selectedMenuIds[0]))
        {
            await RespondToInvalidComponentAsync(
                "This list has expired or belongs to someone else. Run `/role-menu repair` again.",
                cancellation.Token);
            return;
        }

        if (await AcknowledgeEphemeralComponentAsync("Checking the role menu…", cancellation.Token))
        {
            await ShowRepairPreviewAsync(
                menuId, targetChannelId, guild, administrator, bot, cancellation.Token);
        }
    }

    private async Task ShowRepairPageAsync(
        SocketGuild guild,
        ulong targetChannelId,
        RoleMenuPageCursor? cursor,
        CancellationToken cancellationToken)
    {
        var page = await _administration.LoadDeletionPageAsync(guild.Id, cursor, cancellationToken);
        if (page.Menus.Count == 0)
        {
            await ReplaceResponseAsync("There are no role menus in this server yet.", cancellationToken);
            return;
        }

        await ReplaceResponseAsync(
            "Which role menu do you want to repair?",
            cancellationToken,
            components: RoleMenuRepairUi.BuildSelector(
                Context.User.Id, targetChannelId, page,
                channelId => guild.GetChannel(channelId)?.Name));
    }

    private async Task ShowRepairPreviewAsync(
        ObjectId menuId,
        ulong requestedTargetChannelId,
        SocketGuild guild,
        IGuildUser administrator,
        SocketGuildUser bot,
        CancellationToken cancellationToken)
    {
        RoleMenuRepairInspectionResult inspection;
        try
        {
            inspection = await InspectRepairAsync(
                menuId,
                guild.Id,
                bot.Id,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationFailed(_logger, menuId.ToString(), exception);
            await ReplaceResponseAsync(
                "I couldn't check whether the menu message is missing. Try again when Discord is reachable.",
                cancellationToken);
            return;
        }

        if (!inspection.IsEligible)
        {
            await ReplaceResponseAsync(
                FormatRepairInspection(inspection),
                cancellationToken);
            return;
        }

        var settings = inspection.Settings
            ?? throw new InvalidOperationException("An eligible repair did not return saved settings.");
        var parsed = inspection.ParsedSettings
            ?? throw new InvalidOperationException("An eligible repair did not return parsed settings.");
        if (inspection.PanelIssue == RoleMenuRepairPanelIssue.ChannelMissing
            && requestedTargetChannelId == 0)
        {
            await ReplaceResponseAsync(
                "The menu's channel is gone. Run `/role-menu repair` again and choose a `target-channel`.",
                cancellationToken);
            return;
        }

        var targetChannelId = requestedTargetChannelId == 0
            ? parsed.ChannelId
            : requestedTargetChannelId;
        var requestOptions = CreateRequestOptions(cancellationToken);
        var currentAdministrator = await _discord.GetGuildUserAsync(
            guild.Id,
            administrator.Id,
            requestOptions);
        var currentBot = await _discord.GetGuildUserAsync(guild.Id, bot.Id, requestOptions);
        if (currentAdministrator is null || currentBot is null)
        {
            await ReplaceResponseAsync(
                "I couldn't check the current server roles. Try again before repairing this menu.",
                cancellationToken);
            return;
        }

        if (!currentAdministrator.GuildPermissions.ManageRoles)
        {
            await ReplaceResponseAsync(
                "You need **Manage Roles** permission to repair this menu.",
                cancellationToken);
            return;
        }

        var validation = ValidateRoles(parsed.RoleIds, currentAdministrator, currentBot);
        if (!validation.IsValid)
        {
            await ReplaceResponseAsync(
                FormatRoleValidationFailure(validation),
                cancellationToken);
            return;
        }

        var currentTarget = await _discord.GetGuildTextChannelAsync(
            guild.Id,
            targetChannelId,
            requestOptions);
        if (currentTarget is null)
        {
            await ReplaceResponseAsync(
                "That channel is gone or isn't a text channel. Choose another `target-channel` and try again.",
                cancellationToken);
            return;
        }

        var channelPermissionFailure = GetChannelPermissionFailure(currentBot, currentTarget);
        if (channelPermissionFailure is not null)
        {
            await ReplaceResponseAsync(channelPermissionFailure, cancellationToken);
            return;
        }

        await ReplaceResponseAsync(
            "The menu message is missing. Review the details below, then confirm to post a replacement.",
            cancellationToken,
            RoleMenuRepairUi.BuildConfirmationEmbed(
                settings,
                validation.Roles,
                targetChannelId,
                inspection.PanelIssue),
            RoleMenuRepairUi.BuildConfirmationComponents(
                Context.User.Id,
                settings,
                targetChannelId));
    }

    [ComponentInteraction(
        RoleMenuRepairUi.ConfirmPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ConfirmRepairAsync(
        string userIdValue,
        string menuIdValue,
        string targetChannelIdValue,
        string fingerprintValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var boundUserId)
            || boundUserId != Context.User.Id
            || !RoleMenuCustomIds.TryParseMenuId(menuIdValue, out var menuId)
            || !RoleMenuCustomIds.TryParseSnowflake(targetChannelIdValue, out var targetChannelId)
            || fingerprintValue.Length != 24
            || !TryGetGuildActors(out var guild, out _, out var bot)
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(
                component,
                guild,
                ComponentType.Button,
                RoleMenuRepairUi.Confirm(boundUserId, menuId, targetChannelId, fingerprintValue)))
        {
            await RespondToInvalidComponentAsync(
                "This confirmation has expired or belongs to someone else. Run `/role-menu repair` again.",
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
            var mutation = RoleMenus.RunMenuMutationAsync(
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
                        fingerprintValue,
                        operationToken);
                },
                cancellation.Token);
            var feedback = await RoleMenuRepairWorkflow.WaitForMutationAsync(
                mutation, cancellation.Token);
            await SendFreshFeedbackAsync(feedback);
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !RoleMenus.IsShuttingDown)
        {
            await SendFreshFeedbackAsync(
                mutationStarted
                    ? "I ran out of time before I could confirm the result. Check the target channel, then run the same repair again."
                    : "I was busy and couldn't start the repair. Try again.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationFailed(_logger, menuId.ToString(), exception);
            await SendFreshFeedbackAsync(
                "I couldn't confirm the repair result. Check the target channel, then run the same repair again.");
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
                "This confirmation has expired or belongs to someone else. Run `/role-menu repair` again.",
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
                        "Repair cancelled.",
                        null,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    "Repair cancelled.",
                    operationToken),
                cancellation.Token);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Repair cancelled.",
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
        string previewFingerprint,
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
        if (!RoleMenuRepairWorkflow.MatchesPreview(
                initialInspection.Settings!, previewFingerprint))
        {
            return "This menu changed since the preview. Run `/role-menu repair` again to review it.";
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
            return "I couldn't check the current server roles. Try again before repairing this menu.";
        }

        if (!currentAdministrator.GuildPermissions.ManageRoles)
        {
            return "You need **Manage Roles** permission to repair this menu.";
        }

        var targetChannel = await _discord.GetGuildTextChannelAsync(
            guildId,
            targetChannelId,
            requestOptions);
        if (targetChannel is null)
        {
            return "That channel is gone or isn't a text channel. Choose another `target-channel` and try again.";
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
        if (!RoleMenuRepairWorkflow.HasSameSavedConfiguration(initialSettings, settings)
            || !RoleMenuRepairWorkflow.MatchesPreview(settings, previewFingerprint))
        {
            return "This menu changed while you were confirming. Run `/role-menu repair` again to review it.";
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
                "I couldn't find a role menu with that ID in this server. Check the ID and try again.",
            RoleMenuRepairInspectionStatus.SettingsInvalid =>
                "I can't repair this menu because its saved settings are invalid. Check the menu settings before trying again.",
            RoleMenuRepairInspectionStatus.Healthy =>
                "The menu message is still there. There's nothing to repair.",
            RoleMenuRepairInspectionStatus.UnexpectedPanel =>
                "I found a message where this menu should be, but it doesn't match. Check that message before trying again.",
            _ => "The menu message is missing. Run `/role-menu repair` to restore it."
        };

    private static string FormatRepairPublication(
        RoleMenuPublicationResult result,
        ulong guildId,
        ulong targetChannelId)
    {
        if (result.Status == RoleMenuPublicationStatus.Published
            && result.MessageId is ulong messageId)
        {
            return "I repaired the role menu: " +
                   CreateMessageUrl(guildId, targetChannelId, messageId);
        }

        return result.Status switch
        {
            RoleMenuPublicationStatus.PanelOutcomeUnknown =>
                "I couldn't tell whether Discord posted the replacement. Check the target channel, then run the same repair again.",
            RoleMenuPublicationStatus.PersistenceOutcomeUnknown =>
                "I found or posted a replacement message, but couldn't confirm it is connected to the saved menu. Check the target channel, then run the same repair with that channel again.",
            RoleMenuPublicationStatus.PersistenceAbsentPanelRolledBack =>
                "The saved menu disappeared during repair, so I removed the replacement message. Check the menu before trying again.",
            RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed =>
                "The saved menu disappeared, and I couldn't remove the replacement message. Check the target channel before trying again.",
            _ =>
                "I couldn't confirm the repair result. Check the target channel and menu before trying again."
        };
    }
}
