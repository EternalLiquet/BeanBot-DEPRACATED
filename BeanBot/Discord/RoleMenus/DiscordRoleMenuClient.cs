using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using static BeanBot.Discord.RoleMenus.DiscordRoleMenuClient;
using static BeanBot.Discord.RoleMenus.RoleMenuPresentation;

namespace BeanBot.Discord.RoleMenus;

public sealed class DiscordRoleMenuClient
{
    private readonly Func<ulong, ulong, RequestOptions, Task<IGuildUser?>> _getGuildUser;
    private readonly Func<ulong, RequestOptions, Task<IChannel?>> _getChannel;

    public DiscordRoleMenuClient(DiscordSocketClient client)
        : this(async (guildId, userId, options) => await client.Rest.GetGuildUserAsync(guildId, userId, options),
            async (channelId, options) => await client.Rest.GetChannelAsync(channelId, options))
    {
        ArgumentNullException.ThrowIfNull(client);
    }

    internal DiscordRoleMenuClient(
        Func<ulong, ulong, RequestOptions, Task<IGuildUser?>> getGuildUser,
        Func<ulong, RequestOptions, Task<IChannel?>> getChannel)
    {
        _getGuildUser = getGuildUser ?? throw new ArgumentNullException(nameof(getGuildUser));
        _getChannel = getChannel ?? throw new ArgumentNullException(nameof(getChannel));
    }

    internal static RequestOptions CreateRequestOptions(CancellationToken token) => new() { CancelToken = token };

    internal async Task<IGuildUser?> GetGuildUserAsync(
        ulong guildId,
        ulong userId,
        RequestOptions requestOptions)
    {
        try
        {
            return await _getGuildUser(
                guildId,
                userId,
                requestOptions);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    internal async Task<ITextChannel?> GetGuildTextChannelAsync(
        ulong guildId,
        ulong channelId,
        RequestOptions requestOptions)
    {
        try
        {
            var channel = await _getChannel(
                channelId,
                requestOptions);
            return channel is ITextChannel textChannel
                   && textChannel.GuildId == guildId
                   && textChannel.ChannelType == ChannelType.Text
                ? textChannel
                : null;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    internal static async Task<RoleMenuPanelSnapshot?> ReadPublicationPanelAsync(
        ITextChannel targetChannel,
        ulong channelId,
        ulong messageId,
        ObjectId menuId,
        CancellationToken cancellationToken)
    {
        if (targetChannel.Id != channelId)
        {
            return null;
        }

        try
        {
            var message = await targetChannel.GetMessageAsync(
                messageId,
                CacheMode.AllowDownload,
                CreateRequestOptions(cancellationToken));
            return message is IUserMessage userMessage
                ? CreatePanelSnapshot(targetChannel, userMessage, menuId)
                : null;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    internal static async Task<IReadOnlyList<RoleMenuPanelSnapshot>>
        ReadRecentPublicationPanelsAsync(
            ITextChannel targetChannel,
            ulong channelId,
            int maximumResults,
            ObjectId menuId,
            CancellationToken cancellationToken)
    {
        if (targetChannel.Id != channelId)
        {
            return [];
        }

        var messages = await targetChannel
            .GetMessagesAsync(
                maximumResults,
                CacheMode.AllowDownload,
                CreateRequestOptions(cancellationToken))
            .FlattenAsync();
        return messages
            .OfType<IUserMessage>()
            .Select(message => CreatePanelSnapshot(targetChannel, message, menuId))
            .ToList();
    }

    internal static async Task<RoleMenuPanelSnapshot> SendPublicationPanelAsync(
        ITextChannel targetChannel,
        RoleMenuDraft draft,
        CancellationToken cancellationToken)
    {
        var message = await targetChannel.SendMessageAsync(
            embed: RoleMenuComponents.BuildPublicEmbed(
                draft.MenuId,
                draft.Title,
                draft.Description,
                draft.SelectionMode),
            options: CreateRequestOptions(cancellationToken),
            allowedMentions: AllowedMentions.None,
            components: RoleMenuComponents.BuildPublicComponents(draft.MenuId));
        return CreatePanelSnapshot(targetChannel, message, draft.MenuId);
    }

    internal static async Task<bool> RollbackPublicationPanelAsync(
        ITextChannel targetChannel,
        RoleMenuPanelSnapshot panel,
        ObjectId menuId,
        CancellationToken cancellationToken)
    {
        if (panel.GuildId != targetChannel.GuildId
            || panel.ChannelId != targetChannel.Id
            || !panel.HasManageButton)
        {
            return false;
        }

        try
        {
            var message = await targetChannel.GetMessageAsync(
                panel.MessageId,
                CacheMode.AllowDownload,
                CreateRequestOptions(cancellationToken));
            if (message is null)
            {
                return true;
            }

            if (message.Author.Id != panel.AuthorId
                || !RoleMenuComponents.HasManageButton(message, menuId))
            {
                return false;
            }

            await message.DeleteAsync(CreateRequestOptions(cancellationToken));
            return true;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return true;
        }
    }

    internal static RoleMenuPanelSnapshot CreatePanelSnapshot(
        ITextChannel channel,
        IUserMessage message,
        ObjectId menuId)
        => new(
            channel.GuildId,
            channel.Id,
            message.Id,
            message.Author.Id,
            RoleMenuComponents.HasManageButton(message, menuId));

    internal async Task<RoleMenuPanelLookupResult> ReadDeletionPanelAsync(
        ulong guildId,
        ObjectId expectedMenuId,
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        var requestOptions = CreateRequestOptions(cancellationToken);
        IChannel? channel;
        try
        {
            channel = await _getChannel(channelId, requestOptions);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.ChannelMissing);
        }

        if (channel is null)
        {
            return new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.ChannelMissing);
        }

        if (channel is not ITextChannel textChannel || textChannel.GuildId != guildId)
        {
            return new RoleMenuPanelLookupResult(
                RoleMenuPanelLookupStatus.UnexpectedChannelType);
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
            return new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing);
        }

        return message is null
            ? new RoleMenuPanelLookupResult(RoleMenuPanelLookupStatus.MessageMissing)
            : new RoleMenuPanelLookupResult(
                RoleMenuPanelLookupStatus.Found,
                new RoleMenuPanelSnapshot(
                    textChannel.GuildId,
                    textChannel.Id,
                    message.Id,
                    message.Author.Id,
                    RoleMenuComponents.HasManageButton(message, expectedMenuId)));
    }

    internal async Task<bool> DeleteDeletionPanelAsync(
        ulong guildId,
        ObjectId menuId,
        RoleMenuPanelSnapshot panel,
        CancellationToken cancellationToken)
    {
        if (panel.GuildId != guildId || !panel.HasManageButton)
        {
            return false;
        }

        var requestOptions = CreateRequestOptions(cancellationToken);
        try
        {
            var channel = await _getChannel(
                panel.ChannelId,
                requestOptions);
            if (channel is null)
            {
                return true;
            }

            if (channel is not ITextChannel textChannel || textChannel.GuildId != guildId)
            {
                return false;
            }

            var message = await textChannel.GetMessageAsync(
                panel.MessageId,
                CacheMode.AllowDownload,
                requestOptions);
            if (message is null)
            {
                return true;
            }

            if (message.Author.Id != panel.AuthorId
                || !RoleMenuComponents.HasManageButton(message, menuId))
            {
                return false;
            }

            await message.DeleteAsync(requestOptions);
            return true;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return true;
        }
    }

    internal async Task<RoleMenuPanelSnapshot?> ReadPanelSnapshotAsync(
        ulong guildId,
        ObjectId menuId,
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        var requestOptions = CreateRequestOptions(cancellationToken);
        IChannel? channel;
        try
        {
            channel = await _getChannel(channelId, requestOptions);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (channel is not ITextChannel textChannel || textChannel.GuildId != guildId)
        {
            return null;
        }

        try
        {
            var message = await textChannel.GetMessageAsync(
                messageId,
                CacheMode.AllowDownload,
                requestOptions);
            return message is null
                ? null
                : new RoleMenuPanelSnapshot(
                    textChannel.GuildId,
                    textChannel.Id,
                    message.Id,
                    message.Author.Id,
                    RoleMenuComponents.HasManageButton(message, menuId));
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    internal async Task<RoleMenuBotSnapshot?> ReadBotSnapshotAsync(
        ulong guildId,
        ulong botUserId,
        CancellationToken cancellationToken)
    {
        var bot = await GetGuildUserAsync(
            guildId,
            botUserId,
            CreateRequestOptions(cancellationToken));
        if (bot is null)
        {
            return null;
        }

        return new RoleMenuBotSnapshot(
            bot.Guild.Id,
            bot.Id,
            CreateRoleSnapshots(bot),
            CreateActorSnapshot(bot));
    }

    internal static RoleMenuMemberSnapshot CreateMemberSnapshot(IGuildUser member)
        => new(member.Guild.Id, member.Id, member.RoleIds.ToList());

    internal static Task AddMemberRoleAsync(
        IGuildUser? member,
        ulong guildId,
        ulong memberUserId,
        ulong roleId,
        CancellationToken cancellationToken)
    {
        EnsureExpectedMember(member, guildId, memberUserId);
        return member.AddRoleAsync(roleId, CreateRequestOptions(cancellationToken));
    }

    internal static Task RemoveMemberRoleAsync(
        IGuildUser? member,
        ulong guildId,
        ulong memberUserId,
        ulong roleId,
        CancellationToken cancellationToken)
    {
        EnsureExpectedMember(member, guildId, memberUserId);
        return member.RemoveRoleAsync(roleId, CreateRequestOptions(cancellationToken));
    }

    internal static void EnsureExpectedMember(
        [NotNull] IGuildUser? member,
        ulong guildId,
        ulong memberUserId)
    {
        if (member is null
            || member.Guild.Id != guildId
            || member.Id != memberUserId)
        {
            throw new InvalidOperationException(
                "The role-menu member mutation was not bound to the expected guild member.");
        }
    }

    internal static List<RoleMenuRoleSnapshot> CreateRoleSnapshots(
        IGuildUser user)
        => [.. user.Guild.Roles
            .Select(role => new RoleMenuRoleSnapshot(
                role.Id,
                role.Name,
                role.Id == user.Guild.EveryoneRole.Id,
                role.IsManaged,
                role.Position))];

    internal static RoleMenuActorSnapshot CreateActorSnapshot(IGuildUser user)
    {
        var hierarchy = user.Guild.Roles
            .Where(role => user.RoleIds.Contains(role.Id))
            .Select(role => role.Position)
            .DefaultIfEmpty(0)
            .Max();
        return new RoleMenuActorSnapshot(
            user.GuildPermissions.ManageRoles,
            hierarchy,
            user.Guild.OwnerId == user.Id);
    }

}
