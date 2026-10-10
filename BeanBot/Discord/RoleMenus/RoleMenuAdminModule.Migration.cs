using BeanBot.Logging;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using MongoDB.Bson;

using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;

namespace BeanBot.Discord.RoleMenus;

public sealed partial class RoleMenuAdminModule
{
    [SlashCommand(
        "migrate",
        "Preview migration of one legacy reaction-role panel.",
        runMode: RunMode.Sync)]
    public async Task MigrateAsync(
        [Summary("target-channel", "Optional text channel for the new role menu")]
        ITextChannel? targetChannel = null,
        [Summary("title", "Optional replacement title for the new role menu")]
        string? title = null,
        [Summary("description", "Optional description for the new role menu")]
        string? description = null)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        await RoleMenus.ExecuteInitialResponseAsync(
            supportsOriginalResponse: true,
            operationToken => DeferAsync(
                ephemeral: true,
                CreateRequestOptions(operationToken)),
            operationToken => ReplaceResponseAsync(
                "Loading the legacy role panel…",
                operationToken),
            cancellation.Token);

        if (!TryGetGuildActors(out var guild, out var administrator, out _))
        {
            await ReplaceResponseAsync(
                "You can only migrate legacy panels in a server.",
                cancellation.Token);
            return;
        }

        if (!RoleMenus.CreateMigrationSelection(
                guild.Id, administrator.Id, targetChannel?.Id,
                targetChannel?.GuildId, targetChannel?.ChannelType,
                title, description, out var selection) || selection is null)
        {
            await ReplaceResponseAsync(
                "I have too many migration previews open. Try `/role-menu migrate` again shortly.",
                cancellation.Token);
            return;
        }
        await ShowMigrationPageAsync(selection, null, newer: false, cancellation.Token);
    }

    private async Task ShowMigrationPreviewAsync(
        ulong parsedMessageId, ulong guildId, ulong administratorId, ulong botId,
        RoleMenuMigrationSelection selection,
        CancellationToken cancellationToken)
    {
        var preview = await _migration.CreatePreviewAsync(
            new RoleMenuMigrationRequest(
                parsedMessageId,
                selection.TargetChannelId,
                selection.TargetChannelGuildId,
                selection.TargetChannelType,
                selection.Title,
                selection.Description),
            guildId,
            administratorId,
            botId,
            cancellationToken);
        if (preview.Draft is null)
        {
            await ReplaceResponseAsync(preview.Content, cancellationToken);
            return;
        }

        await ReplaceResponseAsync(
            preview.Content,
            cancellationToken,
            RoleMenuComponents.BuildMigrationPreviewEmbed(
                preview.Draft,
                preview.Roles ?? [],
                preview.SourceMessageLink
                    ?? throw new InvalidOperationException("A migration preview did not include its source link.")),
            RoleMenuComponents.BuildMigrationPreviewComponents(preview.Draft.Id));
    }

    [ComponentInteraction(LegacyReactionRoleMigrationPicker.PagePattern,
        ignoreGroupNames: true, runMode: RunMode.Sync)]
    public async Task ChangeMigrationPageAsync(
        string userIdValue, string selectionIdValue,
        string directionValue, string cursorValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var userId)
            || userId != Context.User.Id
            || !RoleMenuCustomIds.TryParseDraftId(selectionIdValue, out var selectionId)
            || !LegacyReactionRoleRetirementPicker.TryParseCursor(
                directionValue, cursorValue, out var newer, out var cursor)
            || Context.Guild is null
            || !RoleMenus.TryGetMigrationSelection(
                selectionId, Context.Guild.Id, userId, out var selection)
            || selection is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(component, Context.Guild,
                ComponentType.Button,
                LegacyReactionRoleMigrationPicker.Page(userId, selectionId, newer, cursor)))
        {
            await RespondToInvalidComponentAsync(
                "This list has expired or belongs to someone else. Run `/role-menu migrate` again.",
                cancellation.Token);
            return;
        }
        if (!await AcknowledgeEphemeralComponentAsync("Loading legacy panels…",
                cancellation.Token))
        {
            return;
        }
        await ShowMigrationPageAsync(selection, cursor, newer, cancellation.Token);
    }

    [ComponentInteraction(LegacyReactionRoleMigrationPicker.SelectPattern,
        ignoreGroupNames: true, runMode: RunMode.Sync)]
    public async Task SelectMigrationPanelAsync(
        string userIdValue, string selectionIdValue, string[] selectedMessageIds)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseSnowflake(userIdValue, out var userId)
            || userId != Context.User.Id
            || !RoleMenuCustomIds.TryParseDraftId(selectionIdValue, out var selectionId)
            || selectedMessageIds is not { Length: 1 }
            || !RoleMenuCustomIds.TryParseSnowflake(selectedMessageIds[0], out var messageId)
            || !TryGetGuildActors(out var guild, out var administrator, out var bot)
            || !RoleMenus.TryGetMigrationSelection(
                selectionId, guild.Id, userId, out var selection)
            || selection is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidManagementComponent(component, guild,
                ComponentType.SelectMenu,
                LegacyReactionRoleMigrationPicker.Select(userId, selectionId), selectedMessageIds[0]))
        {
            await RespondToInvalidComponentAsync(
                "This list has expired or belongs to someone else. Run `/role-menu migrate` again.",
                cancellation.Token);
            return;
        }
        if (!await AcknowledgeEphemeralComponentAsync("Checking that panel…",
                cancellation.Token))
        {
            return;
        }
        await ShowMigrationPreviewAsync(messageId, guild.Id, administrator.Id, bot.Id,
            selection, cancellation.Token);
    }

    private async Task ShowMigrationPageAsync(
        RoleMenuMigrationSelection selection, ObjectId? cursor, bool newer,
        CancellationToken cancellationToken)
    {
        var guild = Context.Guild!;
        var settings = await _legacyReactionRoles.GetGuildPageAsync(
            guild.Id, cursor, newer, LegacyReactionRoleRetirementPicker.PageSize + 1,
            cancellationToken);
        await ReplaceResponseAsync(
            settings.Count == 0 ? "I couldn't find any saved legacy panels on this page."
                : "Which legacy panel do you want to migrate?",
            cancellationToken,
            components: LegacyReactionRoleRetirementPicker.Build(
                Context.User.Id, settings, cursor, newer,
                channelId => guild.GetChannel(channelId)?.Name,
                roleId => guild.GetRole(roleId)?.Name,
                selectId: LegacyReactionRoleMigrationPicker.Select(
                    Context.User.Id, selection.Id),
                pageId: (userId, direction, pageCursor) =>
                    LegacyReactionRoleMigrationPicker.Page(
                        userId, selection.Id, direction, pageCursor)));
    }

    [ComponentInteraction(
        RoleMenuCustomIds.MigrateConfirmPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task ConfirmMigrationAsync(string draftIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseDraftId(draftIdValue, out var draftId)
            || !TryGetGuildActors(out var guild, out var administrator, out var bot)
            || Context.Interaction is not SocketMessageComponent sourceComponent
            || !IsValidPrivateComponent(
                sourceComponent,
                guild,
                ComponentType.Button,
                RoleMenuCustomIds.MigrateConfirm(draftId)))
        {
            await RespondToInvalidComponentAsync(
                "That migration preview is invalid or no longer available.",
                cancellation.Token);
            return;
        }

        var accessStatus = RoleMenus.TryBeginPublish(
            draftId,
            guild.Id,
            administrator.Id,
            out var draft);
        if (accessStatus != RoleMenuDraftAccessStatus.Acquired
            || draft is null
            || draft.LegacyReactionRoleMessageId is null)
        {
            var message = accessStatus switch
            {
                RoleMenuDraftAccessStatus.AlreadyPublishing =>
                    "That migration preview is already being published.",
                RoleMenuDraftAccessStatus.WrongOwner =>
                    "Only the administrator who created this migration preview can publish it.",
                _ => "That migration preview expired or no longer exists. Run `/role-menu migrate` again."
            };
            await RespondToInvalidComponentAsync(message, cancellation.Token);
            return;
        }

        var mutationStarted = false;
        try
        {
            if (!await AcknowledgeEphemeralComponentAsync(
                    "Revalidating and publishing the migrated role menu…",
                    cancellation.Token))
            {
                return;
            }

            var result = await RoleMenus.RunMenuMutationAsync(
                draft.MenuId,
                operationToken =>
                {
                    mutationStarted = true;
                    return _migration.ConfirmAsync(
                        draft,
                        administrator.Id,
                        bot.Id,
                        operationToken);
                },
                cancellation.Token);
            if (result.Completed)
            {
                RoleMenus.CompletePublish(draft.Id, guild.Id, administrator.Id);
            }

            await ReplaceResponseAsync(result.Content, cancellation.Token);
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested && !RoleMenus.IsShuttingDown)
        {
            await SendFreshFeedbackAsync(
                mutationStarted
                    ? "Bean Bot ran out of time and could not confirm the migration result. Inspect " +
                      "the target channel, then rerun `/role-menu migrate` and choose the same legacy panel. " +
                      "The stable migration ID prevents a second saved role menu from being published."
                    : "Bean Bot was busy and did not begin this migration. Run `/role-menu migrate` again.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.RoleMenuPublicationFailed(_logger, draft.MenuId.ToString(), exception);
            await SendFreshFeedbackAsync(
                "Bean Bot couldn't confirm the migration result. Inspect the target channel, then " +
                "rerun `/role-menu migrate` and choose the same legacy panel. Automatic publication retry was not attempted.");
        }
        finally
        {
            RoleMenus.ReleasePublish(draft.Id, guild.Id, administrator.Id);
        }
    }

    [ComponentInteraction(
        RoleMenuCustomIds.MigrateCancelPattern,
        ignoreGroupNames: true,
        runMode: RunMode.Sync)]
    public async Task CancelMigrationAsync(string draftIdValue)
    {
        using var cancellation = RoleMenus.CreateOperationCancellation();
        if (!RoleMenuCustomIds.TryParseDraftId(draftIdValue, out var draftId)
            || Context.Guild is null
            || Context.Interaction is not SocketMessageComponent component
            || !IsValidPrivateComponent(
                component,
                Context.Guild,
                ComponentType.Button,
                RoleMenuCustomIds.MigrateCancel(draftId))
            || !RoleMenus.CancelDraft(draftId, Context.Guild.Id, Context.User.Id))
        {
            await RespondToInvalidComponentAsync(
                "That migration preview expired, belongs to another administrator, or is already publishing.",
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
                        "Legacy role-panel migration cancelled. The source panel was not changed.",
                        null,
                        MessageComponent.Empty),
                    CreateRequestOptions(operationToken)),
                operationToken => ReplaceResponseAsync(
                    "Legacy role-panel migration cancelled. The source panel was not changed.",
                    operationToken),
                cancellation.Token);
            return;
        }

        await RespondToInvalidComponentAsync(
            "Legacy role-panel migration cancelled.",
            cancellation.Token);
    }
}
