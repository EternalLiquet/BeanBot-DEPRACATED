using BeanBot.Entities;
using BeanBot.Repository;
using BeanBot.Util;
using Microsoft.Extensions.Logging;

namespace BeanBot.Services;

public sealed class RoleReactService(IRoleReactRepository roleReactRepository, ILogger<RoleReactService> logger)
{
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly Dictionary<ulong, RoleSettings> _cachedSettings = [];
    private bool _cacheInitialized;

    public async Task SaveRoleSettingsAsync(IEnumerable<RoleEmotePair> roleEmotePairs, ulong guildId, ulong channelId, ulong messageId, CancellationToken cancellationToken = default)
    {
        var normalizedRoleEmotePairs = roleEmotePairs
            .Select(roleEmotePair =>
            {
                roleEmotePair.EmojiKey = roleEmotePair.ResolveEmojiKey();
                return roleEmotePair;
            })
            .ToList();

        var roleSettings = new RoleSettings(normalizedRoleEmotePairs, guildId, channelId, messageId);
        await roleReactRepository.InsertNewRoleSettingsAsync(roleSettings, cancellationToken);

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _cachedSettings[messageId] = roleSettings;
            _cacheInitialized = true;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<ulong?> TryResolveRoleIdAsync(ulong messageId, ulong? emojiId, string? emojiName, CancellationToken cancellationToken = default)
    {
        var emojiKey = ReactionEmojiKey.Create(emojiId, emojiName);
        if (string.IsNullOrWhiteSpace(emojiKey))
        {
            logger.LogDebug("Unable to resolve role mapping for message {MessageId} because the emoji payload did not contain a usable ID or name", messageId);
            return null;
        }

        var roleSettings = await GetRoleSettingsAsync(messageId, cancellationToken);
        var roleId = roleSettings?.RoleEmotePairs
            .FirstOrDefault(roleEmotePair => string.Equals(roleEmotePair.ResolveEmojiKey(), emojiKey, StringComparison.Ordinal))?.RoleId;

        logger.LogDebug(
            "Resolved role mapping for message {MessageId} and emoji key {EmojiKey} (emojiId: {EmojiId}, emojiName: {EmojiName}): {RoleId}",
            messageId,
            emojiKey,
            emojiId,
            emojiName,
            roleId);
        return roleId;
    }

    private async Task<RoleSettings?> GetRoleSettingsAsync(ulong messageId, CancellationToken cancellationToken)
    {
        await EnsureCacheLoadedAsync(cancellationToken);

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedSettings.TryGetValue(messageId, out var cachedSettings))
            {
                return cachedSettings;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        var roleSettings = await roleReactRepository.GetRoleSettingAsync(messageId, cancellationToken);
        if (roleSettings is null)
        {
            return null;
        }

        NormalizeRoleSettings(roleSettings);

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _cachedSettings[messageId] = roleSettings;
        }
        finally
        {
            _cacheLock.Release();
        }

        return roleSettings;
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cacheInitialized)
        {
            return;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cacheInitialized)
            {
                return;
            }

            var roleSettings = await roleReactRepository.GetRecentRoleSettingsAsync(cancellationToken);
            foreach (var setting in roleSettings)
            {
                NormalizeRoleSettings(setting);
                _cachedSettings[setting.MessageId] = setting;
            }

            _cacheInitialized = true;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static void NormalizeRoleSettings(RoleSettings roleSettings)
    {
        foreach (var roleEmotePair in roleSettings.RoleEmotePairs)
        {
            roleEmotePair.EmojiKey = roleEmotePair.ResolveEmojiKey();
        }
    }
}
