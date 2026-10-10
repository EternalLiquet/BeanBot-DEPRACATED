using BeanBot.Discord.ReactionRoles;
using BeanBot.Discord.RoleMenus;
using BeanBot.Logging;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

public sealed class ReactionRoleHandler : IDisposable
{
    private readonly DiscordSocketClient _discordClient;
    private readonly ReactionRoleService _roleService;
    private readonly RoleMenuInteractionService _roleMenus;
    private readonly ILogger<ReactionRoleHandler> _logger;
    private bool _initialized;

    internal bool HasPendingOperations => _roleService.HasPendingOperations;

    public ReactionRoleHandler(
        DiscordSocketClient discordClient,
        ReactionRoleService reactionRoleService,
        RoleMenuInteractionService roleMenus,
        ILogger<ReactionRoleHandler> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _roleService = reactionRoleService ?? throw new ArgumentNullException(nameof(reactionRoleService));
        _roleMenus = roleMenus ?? throw new ArgumentNullException(nameof(roleMenus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        BeanBotLog.ReactHandlerCreated(_logger);
    }

    public void InitializeReactDependentServices()
    {
        if (_initialized)
        {
            return;
        }

        BeanBotLog.RoleServicesCreated(_logger);
        _discordClient.ReactionAdded += _roleService.HandleReact;
        _discordClient.ReactionRemoved += _roleService.HandleRemoveReact;
        _discordClient.MessageDeleted += HandleMessageDeletedAsync;
        _discordClient.MessagesBulkDeleted += HandleMessagesBulkDeletedAsync;
        _initialized = true;
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _discordClient.ReactionAdded -= _roleService.HandleReact;
        _discordClient.ReactionRemoved -= _roleService.HandleRemoveReact;
        _discordClient.MessageDeleted -= HandleMessageDeletedAsync;
        _discordClient.MessagesBulkDeleted -= HandleMessagesBulkDeletedAsync;
        _initialized = false;
        _roleService.Dispose();
    }

    private Task HandleMessageDeletedAsync(
        Cacheable<IMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel)
        => _roleService.TrackHandlerAsync(token => HandleDeletedMessagesAsync([message], channel, token));

    private Task HandleMessagesBulkDeletedAsync(
        IReadOnlyCollection<Cacheable<IMessage, ulong>> messages,
        Cacheable<IMessageChannel, ulong> channel)
        => _roleService.TrackHandlerAsync(token => HandleDeletedMessagesAsync(messages, channel, token));

    private async Task HandleDeletedMessagesAsync(
        IReadOnlyCollection<Cacheable<IMessage, ulong>> messages,
        Cacheable<IMessageChannel, ulong> channel,
        CancellationToken shutdownToken)
    {
        // A deleted message is commonly uncached. The gateway channel identity must be
        // available locally; never fetch deleted content or infer deletion from a fetch failure.
        var guildChannel = channel.HasValue
            ? channel.Value as SocketGuildChannel
            : _discordClient.GetChannel(channel.Id) as SocketGuildChannel;
        if (guildChannel is null)
        {
            return;
        }

        await ProcessDeletedMessageIdsAsync(
            guildChannel.Guild.Id, channel.Id,
            messages.Select(message => message.Id).ToArray(), shutdownToken);
    }

    internal Task HandleDeletedMessageIdsAsync(
        ulong guildId,
        ulong channelId,
        IReadOnlyCollection<ulong> messageIds)
        => _roleService.TrackHandlerAsync(token =>
            ProcessDeletedMessageIdsAsync(guildId, channelId, messageIds, token));

    private async Task ProcessDeletedMessageIdsAsync(
        ulong guildId,
        ulong channelId,
        IReadOnlyCollection<ulong> messageIds,
        CancellationToken shutdownToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            foreach (var messageId in messageIds.Distinct())
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    await _roleService.DeleteSavedPanelAsync(
                        guildId, channelId, messageId, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    BeanBotLog.RolePanelDeletionCleanupFailed(_logger, exception);
                }

                try
                {
                    await _roleMenus.DeleteSavedPanelsForMessageAsync(
                        guildId, channelId, messageId, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    BeanBotLog.RolePanelDeletionCleanupFailed(_logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            BeanBotLog.RolePanelDeletionCleanupTimedOut(_logger);
        }
    }
}
