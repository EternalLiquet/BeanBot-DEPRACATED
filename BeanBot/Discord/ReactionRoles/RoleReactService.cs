using System.Globalization;
using BeanBot.Logging;
using BeanBot.Persistence.Models;
using BeanBot.Persistence.Repositories;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.ReactionRoles;

public class RoleReactService : IDisposable, IAsyncDisposable
{
    private const int DefaultRoleSettingsCacheCapacity = 256;
    private static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly RoleReactRepository _roleReactRepository;
    private readonly DiscordSocketClient? _client;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly BoundedRoleSettingsCache _roleSettings;
    private readonly object _operationSync = new();
    private readonly HashSet<Task> _inFlightOperations = [];
    private readonly TimeSpan _shutdownDrainTimeout;
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger<RoleReactService> _logger;
    private readonly CancellationTokenSource _shutdownCancellation;
    private readonly CancellationToken _shutdownToken;
    private volatile bool _cacheLoaded;
    private bool _stopping;
    private int _disposeStarted;

    public RoleReactService(
        RoleReactRepository roleReactRepository,
        IHostApplicationLifetime applicationLifetime,
        ILogger<RoleReactService> logger,
        DiscordSocketClient? client = null)
        : this(
            roleReactRepository,
            client,
            DefaultShutdownDrainTimeout,
            logger,
            DefaultRoleSettingsCacheCapacity,
            (applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime)))
                .ApplicationStopping)
    {
    }

    internal RoleReactService(
        RoleReactRepository roleReactRepository,
        DiscordSocketClient? client,
        TimeSpan shutdownDrainTimeout,
        ILogger<RoleReactService> logger,
        int cacheCapacity,
        CancellationToken applicationStopping)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(shutdownDrainTimeout, TimeSpan.Zero);
        _roleReactRepository = roleReactRepository ?? throw new ArgumentNullException(nameof(roleReactRepository));
        _client = client;
        _shutdownDrainTimeout = shutdownDrainTimeout;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _roleSettings = new BoundedRoleSettingsCache(cacheCapacity);
        _shutdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        _shutdownToken = _shutdownCancellation.Token;
    }

    internal int CachedRoleSettingsCount => _roleSettings.Count;

    public Task HandleReact(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
        => TrackHandlerAsync(cancellationToken =>
            HandleReactionAsync(message, channel, reaction, addRole: true, cancellationToken));

    public Task HandleRemoveReact(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
        => TrackHandlerAsync(cancellationToken =>
            HandleReactionAsync(message, channel, reaction, addRole: false, cancellationToken));

    internal Task TrackHandlerAsync(Func<CancellationToken, Task> beginOperation)
        => TrackOperationAsync(beginOperation, skipWhenStopping: true);

    private Task TrackRequiredOperationAsync(Func<CancellationToken, Task> beginOperation)
        => TrackOperationAsync(beginOperation, skipWhenStopping: false);

    private Task TrackOperationAsync(
        Func<CancellationToken, Task> beginOperation,
        bool skipWhenStopping)
    {
        ArgumentNullException.ThrowIfNull(beginOperation);

        Task operation;
        lock (_operationSync)
        {
            if (_stopping || _shutdownToken.IsCancellationRequested)
            {
                return skipWhenStopping
                    ? Task.CompletedTask
                    : Task.FromCanceled(_shutdownToken);
            }

            operation = beginOperation(_shutdownToken);
            _inFlightOperations.Add(operation);
        }

        return ObserveOperationAsync(operation);
    }

    private async Task ObserveOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        finally
        {
            lock (_operationSync)
            {
                _inFlightOperations.Remove(operation);
            }
        }
    }

    private async Task HandleReactionAsync(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel,
        SocketReaction reaction,
        bool addRole,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentUser = _client?.CurrentUser;
            if (currentUser == null)
            {
                return;
            }

            var resolvedChannel = await channel.GetOrDownloadAsync();
            if (resolvedChannel is not SocketTextChannel textChannel)
            {
                return;
            }

            var cachedMessage = await message.GetOrDownloadAsync();
            if (cachedMessage.Author.Id != currentUser.Id || reaction.UserId == currentUser.Id)
            {
                return;
            }

            if (reaction.Emote is not Emote customEmote)
            {
                return;
            }

            var roleSetting = await GetCachedRoleSettingAsync(message.Id, cancellationToken);
            var pair = roleSetting?.RoleEmotePairs?
                .FirstOrDefault(candidate =>
                    candidate.EmojiId == customEmote.Id.ToString(CultureInfo.InvariantCulture));
            if (pair == null || !ulong.TryParse(pair.RoleId, out var roleId))
            {
                return;
            }

            var guild = (IGuild)textChannel.Guild;
            var user = await guild.GetUserAsync(reaction.UserId, CacheMode.AllowDownload);
            var role = guild.Roles.FirstOrDefault(candidate => candidate.Id == roleId);
            if (user == null || role == null)
            {
                return;
            }

            if (addRole && !user.RoleIds.Contains(roleId))
            {
                await user.AddRoleAsync(role);
            }
            else if (!addRole && user.RoleIds.Contains(roleId))
            {
                await user.RemoveRoleAsync(role);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BeanBotLog.ReactionRoleActionFailed(
                _logger,
                addRole ? "add" : "remove",
                message.Id,
                exception);
        }
    }

    internal async Task<RoleSettings?> GetCachedRoleSettingAsync(
        ulong messageId,
        CancellationToken cancellationToken)
    {
        await EnsureCacheLoadedAsync(cancellationToken);
        var messageIdText = messageId.ToString(CultureInfo.InvariantCulture);
        if (_roleSettings.TryGet(messageIdText, out var cached))
        {
            return cached;
        }

        var roleSetting = await _roleReactRepository.GetRoleSetting(messageId, cancellationToken);
        if (roleSetting != null && !string.IsNullOrWhiteSpace(roleSetting.MessageId))
        {
            _roleSettings.Set(roleSetting);
        }

        return roleSetting;
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cacheLoaded)
        {
            return;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cacheLoaded)
            {
                return;
            }

            var recentSettings = await _roleReactRepository.GetRecentRoleSettings(
                _roleSettings.Capacity,
                cancellationToken);
            foreach (var setting in recentSettings)
            {
                _roleSettings.Seed(setting);
            }

            _cacheLoaded = true;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public Task SaveRoleSettings(
        List<RoleEmotePair> roleEmotePair,
        IMessage messageToListen,
        CancellationToken cancellationToken = default)
        => TrackRequiredOperationAsync(async shutdownToken =>
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                shutdownToken,
                cancellationToken);
            operationCancellation.Token.ThrowIfCancellationRequested();
            await SaveRoleSettingsCoreAsync(
                roleEmotePair,
                messageToListen,
                operationCancellation.Token);
        });

    private async Task SaveRoleSettingsCoreAsync(
        List<RoleEmotePair> roleEmotePair,
        IMessage messageToListen,
        CancellationToken cancellationToken)
    {
        if (messageToListen.Channel is not SocketTextChannel textChannel)
        {
            throw new InvalidOperationException("Reaction-role settings can only be saved from a guild text channel.");
        }

        var settings = new RoleSettings(
            roleEmotePair,
            textChannel.Guild.Id.ToString(CultureInfo.InvariantCulture),
            messageToListen.Channel.Id.ToString(CultureInfo.InvariantCulture),
            messageToListen.Id.ToString(CultureInfo.InvariantCulture));
        await PersistRoleSettingsAsync(settings, cancellationToken);
    }

    internal async Task PersistRoleSettingsAsync(
        RoleSettings settings,
        CancellationToken cancellationToken)
    {
        await _roleReactRepository.InsertNewRoleSettings(settings, cancellationToken);
        _roleSettings.Set(settings);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _disposeCompletion.Task;
            return;
        }

        try
        {
            _shutdownCancellation.Cancel();

            Task[] inFlightOperations;
            lock (_operationSync)
            {
                _stopping = true;
                inFlightOperations = [.. _inFlightOperations];
            }

            if (inFlightOperations.Length > 0)
            {
                try
                {
                    await Task.WhenAll(inFlightOperations).WaitAsync(_shutdownDrainTimeout);
                }
                catch (TimeoutException)
                {
                    BeanBotLog.ReactionRoleDrainTimedOut(_logger, inFlightOperations.Length);
                    return;
                }
                catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    BeanBotLog.ReactionRoleShutdownOperationFailed(_logger, exception);
                }
            }

            _cacheLock.Dispose();
            _shutdownCancellation.Dispose();
            GC.SuppressFinalize(this);
        }
        finally
        {
            _disposeCompletion.TrySetResult();
        }
    }
}
