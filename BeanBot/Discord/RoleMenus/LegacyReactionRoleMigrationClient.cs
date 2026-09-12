using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace BeanBot.Discord.RoleMenus;

internal enum LegacyReactionRolePanelLookupStatus
{
    Found,
    ChannelMissing,
    MessageMissing,
    Unrecognized
}

internal sealed record LegacyReactionRolePanelLookupResult(
    LegacyReactionRolePanelLookupStatus Status,
    string? SuggestedTitle = null);

internal sealed class LegacyReactionRoleMigrationClient
{
    private readonly Func<ulong, RequestOptions, Task<IChannel?>> _getChannel;

    public LegacyReactionRoleMigrationClient(DiscordSocketClient client)
        : this(async (channelId, options) => await client.Rest.GetChannelAsync(channelId, options))
    {
        ArgumentNullException.ThrowIfNull(client);
    }

    internal LegacyReactionRoleMigrationClient(
        Func<ulong, RequestOptions, Task<IChannel?>> getChannel)
    {
        _getChannel = getChannel ?? throw new ArgumentNullException(nameof(getChannel));
    }

    internal async Task<LegacyReactionRolePanelLookupResult> ReadSourcePanelAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong botUserId,
        IReadOnlyCollection<ulong> expectedRoleIds,
        CancellationToken cancellationToken)
    {
        var requestOptions = DiscordRoleMenuClient.CreateRequestOptions(cancellationToken);
        IChannel? channel;
        try
        {
            channel = await _getChannel(channelId, requestOptions);
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

        if (channel is not ITextChannel textChannel || textChannel.GuildId != guildId)
        {
            return new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.Unrecognized);
        }

        IMessage? message;
        try
        {
            message = await textChannel.GetMessageAsync(
                messageId,
                CacheMode.AllowDownload,
                requestOptions);
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

        return LegacyReactionRolePanelIdentity.TryRecognize(
            message.Author.Id,
            botUserId,
            message.Embeds,
            expectedRoleIds,
            out var suggestedTitle)
            ? new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.Found,
                suggestedTitle)
            : new LegacyReactionRolePanelLookupResult(
                LegacyReactionRolePanelLookupStatus.Unrecognized);
    }
}
