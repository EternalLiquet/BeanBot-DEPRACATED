using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Services.Commands;

namespace BeanBot.Services;

public sealed class MessagePromptService(ILogger<MessagePromptService> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<PromptKey, PromptRegistration> _registrations = [];

    public Task<Message?> WaitForNextMessageAsync(CommandContext context, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var channelId = context.Channel?.Id ?? context.Message.Channel?.Id;
        if (channelId is not ulong resolvedChannelId)
        {
            return Task.FromResult<Message?>(null);
        }

        var key = new PromptKey(resolvedChannelId, context.User.Id);
        var registration = new PromptRegistration(
            key,
            new TaskCompletionSource<Message?>(TaskCreationOptions.RunContinuationsAsynchronously));

        lock (_lock)
        {
            if (_registrations.Remove(key, out var existingRegistration))
            {
                logger.LogDebug(
                    "Replacing existing prompt registration for user {UserId} in channel {ChannelId}",
                    key.UserId,
                    key.ChannelId);
                existingRegistration.CompletionSource.TrySetResult(null);
            }

            _registrations[key] = registration;
        }

        logger.LogDebug(
            "Registered prompt for user {UserId} in channel {ChannelId} with timeout {Timeout}",
            key.UserId,
            key.ChannelId,
            timeout);

        return WaitAsync(registration, timeout, cancellationToken);
    }

    public void PublishMessage(Message message)
    {
        if (message.Author is null || message.Channel is null || message.Author.IsBot)
        {
            return;
        }

        var key = new PromptKey(message.Channel.Id, message.Author.Id);
        PromptRegistration? registrationToComplete = null;
        lock (_lock)
        {
            _registrations.TryGetValue(key, out registrationToComplete);
        }

        if (registrationToComplete is null)
        {
            return;
        }

        logger.LogDebug("Matched prompt response for user {UserId} in channel {ChannelId}", message.Author.Id, message.Channel.Id);
        registrationToComplete.CompletionSource.TrySetResult(message);
    }

    private async Task<Message?> WaitAsync(PromptRegistration registration, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        using var registrationHandle = timeoutSource.Token.Register(() => registration.CompletionSource.TrySetResult(null));
        try
        {
            return await registration.CompletionSource.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _registrations.Remove(registration.Key);
            }
        }
    }

    private readonly record struct PromptKey(ulong ChannelId, ulong UserId);

    private sealed record PromptRegistration(PromptKey Key, TaskCompletionSource<Message?> CompletionSource);
}
