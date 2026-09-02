using System.Buffers;
using BeanBot.Logging;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

public sealed class EditMessageEventServices
{
    internal const string EditWarning = "Do not edit your 8ball requests in my presence, mortal.";
    internal static readonly TimeSpan DiscordOperationTimeout = TimeSpan.FromSeconds(10);
    private const int MaximumReplaceAttempts = 3;
    private static readonly SearchValues<char> CommandSeparators = SearchValues.Create(" \t\r\n");
    private readonly DiscordSocketClient _discordClient;
    private readonly ILogger<EditMessageEventServices> _logger;

    public EditMessageEventServices(
        DiscordSocketClient discordClient,
        ILogger<EditMessageEventServices> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal async Task HandleUpdate(
        Cacheable<IMessage, ulong> oldMessage,
        SocketMessage newMessage,
        ISocketMessageChannel messageChannel,
        CancellationToken cancellationToken,
        Action<Task>? trackLateDiscordOperation = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var currentUser = _discordClient.CurrentUser;
        if (currentUser is null)
        {
            return;
        }

        IMessage? previousMessage;
        try
        {
            previousMessage = oldMessage.HasValue
                ? oldMessage.Value
                : await ResolvePreviousMessageAsync(
                    options => messageChannel.GetMessageAsync(
                        oldMessage.Id,
                        CacheMode.AllowDownload,
                        options),
                    cancellationToken,
                    DiscordOperationTimeout,
                    trackLateDiscordOperation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            EditMessageLog.OriginalMessageLookupFailed(_logger, oldMessage.Id, exception);
            return;
        }

        if (previousMessage is null ||
            !IsFortuneCommand(previousMessage.Content, currentUser.Id))
        {
            return;
        }

        var botResponse = messageChannel.CachedMessages
            .OfType<SocketUserMessage>()
            .Where(message => message.Author.Id == currentUser.Id)
            .Where(message => message.Id > newMessage.Id)
            .Where(message => message.Timestamp - newMessage.Timestamp <= TimeSpan.FromMinutes(2))
            .OrderBy(message => message.Id)
            .FirstOrDefault();

        if (botResponse is null)
        {
            BeanBotLog.FortuneResponseMissing(_logger, newMessage.Id);
            return;
        }

        await ReplaceResponseAsync(
            (content, options) => botResponse.ModifyAsync(
                properties => properties.Content = content,
                options),
            botResponse.Id,
            _logger,
            cancellationToken,
            DiscordOperationTimeout,
            trackLateDiscordOperation: trackLateDiscordOperation).ConfigureAwait(false);
    }

    internal static bool IsFortuneCommand(string content, ulong botUserId = 0)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var commandText = content.TrimStart();
        if (commandText[0] == '%')
        {
            commandText = commandText.Substring(1).TrimStart();
        }
        else if (commandText.StartsWith("succ ", StringComparison.OrdinalIgnoreCase))
        {
            commandText = commandText.Substring("succ ".Length).TrimStart();
        }
        else if (botUserId != 0 &&
                 (commandText.StartsWith($"<@{botUserId}> ", StringComparison.Ordinal) ||
                  commandText.StartsWith($"<@!{botUserId}> ", StringComparison.Ordinal)))
        {
            var mentionEnd = commandText.IndexOf('>');
            commandText = commandText.Substring(mentionEnd + 1).TrimStart();
        }
        else
        {
            return false;
        }

        var commandEnd = commandText.AsSpan().IndexOfAny(CommandSeparators);
        var command = commandEnd < 0 ? commandText : commandText.Substring(0, commandEnd);
        return string.Equals(command, "8ball", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(command, "fortune", StringComparison.OrdinalIgnoreCase);
    }

    internal static Task<IMessage?> ResolvePreviousMessageAsync(
        Func<RequestOptions, Task<IMessage?>> resolveMessage,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null,
        Action<Task>? trackLateDiscordOperation = null)
    {
        ArgumentNullException.ThrowIfNull(resolveMessage);

        return RunBoundedDiscordOperationAsync(
            resolveMessage,
            operationTimeout ?? DiscordOperationTimeout,
            cancellationToken,
            trackLateDiscordOperation);
    }

    internal static async Task ReplaceResponseAsync(
        Func<string, RequestOptions, Task> modifyResponse,
        ulong messageId,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<Task>? trackLateDiscordOperation = null)
    {
        ArgumentNullException.ThrowIfNull(modifyResponse);
        ArgumentNullException.ThrowIfNull(logger);

        var timeout = operationTimeout ?? DiscordOperationTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "Operation timeout must be greater than zero.");
        }

        delayAsync ??= static (delay, token) => Task.Delay(delay, token);

        for (var attempt = 1; attempt <= MaximumReplaceAttempts; attempt++)
        {
            try
            {
                await RunBoundedDiscordOperationAsync(
                    options => modifyResponse(EditWarning, options),
                    timeout,
                    cancellationToken,
                    trackLateDiscordOperation).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException exception)
            {
                BeanBotLog.FortuneResponseReplaceFailed(logger, messageId, exception);
                return;
            }
            catch (OperationCanceledException exception)
            {
                BeanBotLog.FortuneResponseReplaceFailed(logger, messageId, exception);
                return;
            }
            catch (Exception exception) when (attempt < MaximumReplaceAttempts)
            {
                BeanBotLog.FortuneResponseReplaceAttemptFailed(logger, attempt, exception);
                await delayAsync(
                    TimeSpan.FromMilliseconds(100 * attempt),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                BeanBotLog.FortuneResponseReplaceFailed(logger, messageId, exception);
            }
        }
    }

    internal static async Task<T> RunBoundedDiscordOperationAsync<T>(
        Func<RequestOptions, Task<T>> beginOperation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<Task>? trackLateDiscordOperation = null)
    {
        ArgumentNullException.ThrowIfNull(beginOperation);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Operation timeout must be greater than zero.");
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(timeout);
        var requestOptions = new RequestOptions
        {
            CancelToken = operationCancellation.Token
        };

        var operation = beginOperation(requestOptions);
        try
        {
            return await operation
                .WaitAsync(operationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TrackLateOperation(operation, trackLateDiscordOperation);
            throw;
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            TrackLateOperation(operation, trackLateDiscordOperation);
            throw new TimeoutException(
                $"Discord edited-message operation exceeded its {timeout} timeout.");
        }
    }

    internal static async Task RunBoundedDiscordOperationAsync(
        Func<RequestOptions, Task> beginOperation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<Task>? trackLateDiscordOperation = null)
    {
        await RunBoundedDiscordOperationAsync(
            async options =>
            {
                await beginOperation(options).ConfigureAwait(false);
                return true;
            },
            timeout,
            cancellationToken,
            trackLateDiscordOperation).ConfigureAwait(false);
    }

    private static void TrackLateOperation(
        Task operation,
        Action<Task>? trackLateDiscordOperation)
    {
        if (operation.IsCompleted)
        {
            _ = operation.Exception;
            return;
        }

        if (trackLateDiscordOperation is not null)
        {
            trackLateDiscordOperation(operation);
            return;
        }

        _ = operation.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
