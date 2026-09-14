using System.Globalization;
using System.Net;
using BeanBot.Persistence.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace BeanBot.Discord.RoleMenus;

internal sealed record LegacyReactionRoleSource(
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    IReadOnlyList<ulong> RoleIds);

internal static class LegacyReactionRoleSourceParser
{
    internal static bool TryParse(
        ReactionRoleSettings settings,
        out LegacyReactionRoleSource? source)
    {
        ArgumentNullException.ThrowIfNull(settings);
        source = null;
        if (!TryParseSnowflake(settings.GuildId, out var guildId)
            || !TryParseSnowflake(settings.ChannelId, out var channelId)
            || !TryParseSnowflake(settings.MessageId, out var messageId)
            || settings.RoleEmotePairs.Count is < 1 or > 25)
        {
            return false;
        }

        var roleIds = new List<ulong>(settings.RoleEmotePairs.Count);
        foreach (var pair in settings.RoleEmotePairs)
        {
            if (pair is null
                || !TryParseSnowflake(pair.RoleId, out var roleId)
                || roleIds.Contains(roleId))
            {
                return false;
            }

            roleIds.Add(roleId);
        }

        source = new LegacyReactionRoleSource(guildId, channelId, messageId, roleIds);
        return true;
    }

    private static bool TryParseSnowflake(string value, out ulong snowflake)
        => ulong.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out snowflake)
            && snowflake != 0;
}

internal enum LegacyReactionRolePanelLookupStatus
{
    Found,
    ChannelMissing,
    MessageMissing,
    UnexpectedChannel,
    Unrecognized
}

internal sealed record LegacyReactionRolePanelSnapshot(
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    ulong AuthorId,
    IReadOnlyCollection<IEmbed> Embeds);

internal sealed record LegacyReactionRolePanelLookupResult(
    LegacyReactionRolePanelLookupStatus Status,
    LegacyReactionRolePanelSnapshot? Panel = null,
    string? SuggestedTitle = null);

internal sealed class LegacyReactionRoleRetirementClient
{
    private readonly Func<ulong, RequestOptions, Task<IChannel?>> _getChannel;
    private readonly Func<ulong, ulong, RequestOptions, Task<IGuildUser?>> _getGuildUser;

    internal LegacyReactionRoleRetirementClient(DiscordSocketClient client)
        : this(
            async (channelId, options) => await client.Rest.GetChannelAsync(channelId, options),
            async (guildId, userId, options) =>
                await client.Rest.GetGuildUserAsync(guildId, userId, options))
    {
        ArgumentNullException.ThrowIfNull(client);
    }

    internal LegacyReactionRoleRetirementClient(
        Func<ulong, RequestOptions, Task<IChannel?>> getChannel,
        Func<ulong, ulong, RequestOptions, Task<IGuildUser?>> getGuildUser)
    {
        _getChannel = getChannel ?? throw new ArgumentNullException(nameof(getChannel));
        _getGuildUser = getGuildUser ?? throw new ArgumentNullException(nameof(getGuildUser));
    }

    internal async Task<bool?> CanAdministratorManageRolesAsync(
        ulong guildId,
        ulong administratorId,
        CancellationToken cancellationToken)
    {
        var options = DiscordRoleMenuClient.CreateRequestOptions(cancellationToken);
        try
        {
            var administrator = await _getGuildUser(guildId, administratorId, options);
            return administrator is not null
                   && administrator.Guild.Id == guildId
                ? administrator.GuildPermissions.ManageRoles
                : null;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    internal async Task<LegacyReactionRolePanelLookupResult> ReadPanelAsync(
        LegacyReactionRoleSource source,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = DiscordRoleMenuClient.CreateRequestOptions(cancellationToken);
        IChannel? channel;
        try
        {
            channel = await _getChannel(source.ChannelId, options);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.ChannelMissing);
        }

        if (channel is null)
        {
            return new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.ChannelMissing);
        }

        if (channel is not ITextChannel textChannel || textChannel.GuildId != source.GuildId)
        {
            return new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.UnexpectedChannel);
        }

        IMessage? message;
        try
        {
            message = await textChannel.GetMessageAsync(
                source.MessageId,
                CacheMode.AllowDownload,
                options);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.MessageMissing);
        }

        if (message is null)
        {
            return new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.MessageMissing);
        }

        var panel = new LegacyReactionRolePanelSnapshot(
            textChannel.GuildId,
            textChannel.Id,
            message.Id,
            message.Author.Id,
            message.Embeds);
        return LegacyReactionRolePanelIdentity.TryRecognize(
            panel.AuthorId,
            botUserId,
            panel.Embeds,
            source.RoleIds,
            out var suggestedTitle)
            ? new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.Found,
                panel,
                suggestedTitle)
            : new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.Unrecognized,
                panel);
    }

    internal async Task<bool> DeletePanelAsync(
        LegacyReactionRolePanelSnapshot panel,
        ulong botUserId,
        IReadOnlyCollection<ulong> expectedRoleIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(expectedRoleIds);
        var options = DiscordRoleMenuClient.CreateRequestOptions(cancellationToken);
        try
        {
            var channel = await _getChannel(panel.ChannelId, options);
            if (channel is null)
            {
                return true;
            }

            if (channel is not ITextChannel textChannel
                || textChannel.GuildId != panel.GuildId)
            {
                return false;
            }

            var message = await textChannel.GetMessageAsync(
                panel.MessageId,
                CacheMode.AllowDownload,
                options);
            if (message is null)
            {
                return true;
            }

            if (!LegacyReactionRolePanelIdentity.TryRecognize(
                    message.Author.Id,
                    botUserId,
                    message.Embeds,
                    expectedRoleIds,
                    out _))
            {
                return false;
            }

            await message.DeleteAsync(options);
            return true;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return true;
        }
    }
}

internal enum LegacyReactionRoleRetirementStatus
{
    Retired,
    AlreadyRetired,
    AuthorizationDenied,
    InvalidSavedConfiguration,
    UnsafeSource,
    PanelDeletionFailed,
    PanelOutcomeUnknown,
    PersistenceKept,
    PersistenceOutcomeUnknown
}

internal sealed record LegacyReactionRoleRetirementResult(
    LegacyReactionRoleRetirementStatus Status,
    bool SourceWasMissing = false,
    Exception? Failure = null,
    Exception? ReconciliationFailure = null);

internal sealed record LegacyReactionRoleRetirementOperations(
    Func<ulong, CancellationToken, Task<ReactionRoleSettings?>> ReadSettings,
    Func<CancellationToken, Task<bool?>> ReadAdministratorCanManageRoles,
    Func<LegacyReactionRoleSource, CancellationToken, Task<LegacyReactionRolePanelLookupResult>> ReadPanel,
    Func<LegacyReactionRolePanelSnapshot, IReadOnlyCollection<ulong>, CancellationToken, Task<bool>> DeletePanel,
    Func<ulong, ulong, CancellationToken, Task<bool>> DeleteSettings,
    Func<bool> IsShuttingDown);

internal static class LegacyReactionRoleRetirementWorkflow
{
    internal static async Task<LegacyReactionRoleRetirementResult> ExecuteAsync(
        ulong messageId,
        ulong guildId,
        LegacyReactionRoleRetirementOperations operations,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(messageId);
        ArgumentOutOfRangeException.ThrowIfZero(guildId);
        ArgumentNullException.ThrowIfNull(operations);
        ValidateOperations(operations);

        var canManageRoles = await operations.ReadAdministratorCanManageRoles(cancellationToken);
        if (canManageRoles != true)
        {
            return new LegacyReactionRoleRetirementResult(
                LegacyReactionRoleRetirementStatus.AuthorizationDenied);
        }

        var settings = await operations.ReadSettings(messageId, cancellationToken);
        if (settings is null)
        {
            return new LegacyReactionRoleRetirementResult(
                LegacyReactionRoleRetirementStatus.AlreadyRetired);
        }

        if (!LegacyReactionRoleSourceParser.TryParse(settings, out var source)
            || source is null
            || source.GuildId != guildId
            || source.MessageId != messageId)
        {
            return new LegacyReactionRoleRetirementResult(
                LegacyReactionRoleRetirementStatus.InvalidSavedConfiguration);
        }

        LegacyReactionRolePanelLookupResult lookup;
        try
        {
            lookup = await operations.ReadPanel(source, cancellationToken);
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception exception)
        {
            return new LegacyReactionRoleRetirementResult(
                LegacyReactionRoleRetirementStatus.PanelOutcomeUnknown,
                Failure: exception);
        }

        var sourceWasMissing = lookup.Status is LegacyReactionRolePanelLookupStatus.ChannelMissing
            or LegacyReactionRolePanelLookupStatus.MessageMissing;
        if (!sourceWasMissing)
        {
            if (lookup.Status is LegacyReactionRolePanelLookupStatus.UnexpectedChannel
                or LegacyReactionRolePanelLookupStatus.Unrecognized
                || lookup.Panel is null)
            {
                return new LegacyReactionRoleRetirementResult(
                    LegacyReactionRoleRetirementStatus.UnsafeSource);
            }

            try
            {
                var deleted = await operations.DeletePanel(
                    lookup.Panel,
                    source.RoleIds,
                    cancellationToken);
                if (!deleted)
                {
                    return new LegacyReactionRoleRetirementResult(
                        LegacyReactionRoleRetirementStatus.PanelDeletionFailed);
                }
            }
            catch (OperationCanceledException) when (operations.IsShuttingDown())
            {
                throw;
            }
            catch (Exception exception)
            {
                return await ReconcilePanelAsync(
                    source,
                    operations,
                    exception);
            }
        }

        return await DeleteSettingsAsync(
            messageId,
            guildId,
            sourceWasMissing,
            operations,
            cancellationToken);
    }

    private static async Task<LegacyReactionRoleRetirementResult> ReconcilePanelAsync(
        LegacyReactionRoleSource source,
        LegacyReactionRoleRetirementOperations operations,
        Exception deletionFailure)
    {
        using var reconciliationCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        try
        {
            var lookup = await operations.ReadPanel(
                source,
                reconciliationCancellation.Token);
            if (lookup.Status is LegacyReactionRolePanelLookupStatus.ChannelMissing
                or LegacyReactionRolePanelLookupStatus.MessageMissing)
            {
                return new LegacyReactionRoleRetirementResult(
                    LegacyReactionRoleRetirementStatus.PanelOutcomeUnknown,
                    SourceWasMissing: true,
                    Failure: deletionFailure);
            }

            return new LegacyReactionRoleRetirementResult(
                LegacyReactionRoleRetirementStatus.PanelDeletionFailed,
                Failure: deletionFailure);
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception reconciliationFailure)
        {
            return new LegacyReactionRoleRetirementResult(
                LegacyReactionRoleRetirementStatus.PanelOutcomeUnknown,
                Failure: deletionFailure,
                ReconciliationFailure: reconciliationFailure);
        }
    }

    private static async Task<LegacyReactionRoleRetirementResult> DeleteSettingsAsync(
        ulong messageId,
        ulong guildId,
        bool sourceWasMissing,
        LegacyReactionRoleRetirementOperations operations,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await operations.DeleteSettings(messageId, guildId, cancellationToken);
            if (deleted)
            {
                return new LegacyReactionRoleRetirementResult(
                    LegacyReactionRoleRetirementStatus.Retired,
                    sourceWasMissing);
            }

            return await ReconcileSettingsDeletionAsync(
                messageId,
                sourceWasMissing,
                operations,
                deletionFailure: null);
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception exception)
        {
            return await ReconcileSettingsDeletionAsync(
                messageId,
                sourceWasMissing,
                operations,
                exception);
        }
    }

    private static async Task<LegacyReactionRoleRetirementResult> ReconcileSettingsDeletionAsync(
        ulong messageId,
        bool sourceWasMissing,
        LegacyReactionRoleRetirementOperations operations,
        Exception? deletionFailure)
    {
        using var reconciliationCancellation = new CancellationTokenSource(
            RoleMenuConstants.CleanupTimeout);
        try
        {
            var remaining = await operations.ReadSettings(
                messageId,
                reconciliationCancellation.Token);
            return remaining is null
                ? new LegacyReactionRoleRetirementResult(
                    LegacyReactionRoleRetirementStatus.Retired,
                    sourceWasMissing,
                    Failure: deletionFailure)
                : new LegacyReactionRoleRetirementResult(
                    LegacyReactionRoleRetirementStatus.PersistenceKept,
                    sourceWasMissing,
                    Failure: deletionFailure);
        }
        catch (OperationCanceledException) when (operations.IsShuttingDown())
        {
            throw;
        }
        catch (Exception reconciliationFailure)
        {
            return new LegacyReactionRoleRetirementResult(
                LegacyReactionRoleRetirementStatus.PersistenceOutcomeUnknown,
                sourceWasMissing,
                Failure: deletionFailure,
                ReconciliationFailure: reconciliationFailure);
        }
    }

    private static void ValidateOperations(LegacyReactionRoleRetirementOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations.ReadSettings);
        ArgumentNullException.ThrowIfNull(operations.ReadAdministratorCanManageRoles);
        ArgumentNullException.ThrowIfNull(operations.ReadPanel);
        ArgumentNullException.ThrowIfNull(operations.DeletePanel);
        ArgumentNullException.ThrowIfNull(operations.DeleteSettings);
        ArgumentNullException.ThrowIfNull(operations.IsShuttingDown);
    }
}
