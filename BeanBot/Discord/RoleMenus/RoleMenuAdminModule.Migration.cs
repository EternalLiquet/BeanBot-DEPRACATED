using BeanBot.Logging;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;

namespace BeanBot.Discord.RoleMenus;

public sealed partial class RoleMenuAdminModule
{
    [SlashCommand(
        "migrate",
        "Preview migration of one legacy reaction-role panel.",
        runMode: RunMode.Sync)]
    public async Task MigrateAsync(
        [Summary("legacy-message-id", "Message ID of the saved legacy reaction-role panel")]
        string legacyMessageId,
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

        if (!RoleMenuCustomIds.TryParseSnowflake(legacyMessageId, out var parsedMessageId))
        {
            await ReplaceResponseAsync(
                "That legacy message ID is invalid. Copy the numeric ID from the legacy reaction-role message.",
                cancellation.Token);
            return;
        }

        if (!TryGetGuildActors(out var guild, out var administrator, out var bot))
        {
            await ReplaceResponseAsync(
                "Legacy role panels can only be migrated inside a server.",
                cancellation.Token);
            return;
        }

        var preview = await _migration.CreatePreviewAsync(
            new RoleMenuMigrationRequest(
                parsedMessageId,
                targetChannel?.Id,
                targetChannel?.GuildId,
                targetChannel?.ChannelType,
                title,
                description),
            guild.Id,
            administrator.Id,
            bot.Id,
            cancellation.Token);
        if (preview.Draft is null)
        {
            await ReplaceResponseAsync(preview.Content, cancellation.Token);
            return;
        }

        await ReplaceResponseAsync(
            preview.Content,
            cancellation.Token,
            RoleMenuComponents.BuildMigrationPreviewEmbed(
                preview.Draft,
                preview.Roles ?? [],
                preview.SourceMessageLink
                    ?? throw new InvalidOperationException("A migration preview did not include its source link.")),
            RoleMenuComponents.BuildMigrationPreviewComponents(preview.Draft.Id));
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
                      "the target channel, then rerun `/role-menu migrate` for the same legacy message. " +
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
                "rerun `/role-menu migrate` for the same legacy message. Automatic publication retry was not attempted.");
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
