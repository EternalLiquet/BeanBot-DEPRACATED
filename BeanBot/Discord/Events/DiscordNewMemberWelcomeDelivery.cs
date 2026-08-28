using BeanBot.Configuration;
using BeanBot.Logging;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Events;

internal interface INewMemberWelcomeDelivery
{
    bool HasActiveOperation { get; }
    Task DeliverAsync(ulong userId, CancellationToken cancellationToken);
}

internal sealed class DiscordNewMemberWelcomeDelivery : INewMemberWelcomeDelivery
{
    private readonly Func<ulong, RequestOptions, Task<IDMChannel>> _createDmChannel;
    private readonly Func<IDMChannel, string, RequestOptions, Task> _sendMessage;
    private readonly NewMemberWelcomeOptions _welcomeOptions;
    private readonly NewMemberWelcomeRuntimeOptions _runtimeOptions;
    private readonly ILogger<DiscordNewMemberWelcomeDelivery> _logger;
    private int _availableOperationSlots;
    private int _activeOperationCount;

    public DiscordNewMemberWelcomeDelivery(
        DiscordSocketClient discordClient,
        NewMemberWelcomeOptions welcomeOptions,
        NewMemberWelcomeRuntimeOptions runtimeOptions,
        ILogger<DiscordNewMemberWelcomeDelivery> logger)
        : this(
            (userId, requestOptions) =>
            {
                var user = discordClient.GetUser(userId) ??
                    throw new InvalidOperationException(
                        $"Discord user {userId} is no longer available for welcome delivery.");
                return user.CreateDMChannelAsync(requestOptions);
            },
            async (channel, message, requestOptions) =>
                await channel.SendMessageAsync(message, options: requestOptions),
            welcomeOptions,
            runtimeOptions,
            logger)
    {
        ArgumentNullException.ThrowIfNull(discordClient);
    }

    internal DiscordNewMemberWelcomeDelivery(
        Func<ulong, RequestOptions, Task<IDMChannel>> createDmChannel,
        Func<IDMChannel, string, RequestOptions, Task> sendMessage,
        NewMemberWelcomeOptions welcomeOptions,
        NewMemberWelcomeRuntimeOptions runtimeOptions,
        ILogger<DiscordNewMemberWelcomeDelivery> logger)
    {
        _createDmChannel = createDmChannel ?? throw new ArgumentNullException(nameof(createDmChannel));
        _sendMessage = sendMessage ?? throw new ArgumentNullException(nameof(sendMessage));
        _welcomeOptions = welcomeOptions ?? throw new ArgumentNullException(nameof(welcomeOptions));
        _runtimeOptions = runtimeOptions ?? throw new ArgumentNullException(nameof(runtimeOptions));
        _runtimeOptions.Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _availableOperationSlots = _runtimeOptions.MaximumDiscordOperations;
    }

    public bool HasActiveOperation => Volatile.Read(ref _activeOperationCount) > 0;

    public async Task DeliverAsync(ulong userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = new RequestOptions { CancelToken = cancellationToken };

        var dmChannel = await RunBoundedOperationAsync(
            () => _createDmChannel(userId, requestOptions),
            "create-dm-channel",
            userId,
            cancellationToken);

        await RunBoundedOperationAsync(
            () => _sendMessage(dmChannel, _welcomeOptions.Message, requestOptions),
            "send-message",
            userId,
            cancellationToken);
    }

    private async Task<T> RunBoundedOperationAsync<T>(
        Func<Task<T>> beginOperation,
        string operationName,
        ulong userId,
        CancellationToken cancellationToken)
    {
        if (!TryAcquireOperationSlot())
        {
            throw new InvalidOperationException(
                "The bounded new-member Discord operation capacity is exhausted.");
        }

        Task<T> operation;
        try
        {
            operation = beginOperation();
        }
        catch
        {
            ReleaseOperationSlot();
            throw;
        }

        var releaseOnReturn = true;
        try
        {
            return await operation.WaitAsync(_runtimeOptions.OperationTimeout, cancellationToken);
        }
        catch
        {
            if (!operation.IsCompleted)
            {
                releaseOnReturn = false;
                ObserveLateCompletionAndRelease(operation, operationName, userId);
            }
            else
            {
                _ = operation.Exception;
            }

            throw;
        }
        finally
        {
            if (releaseOnReturn)
            {
                ReleaseOperationSlot();
            }
        }
    }

    private async Task RunBoundedOperationAsync(
        Func<Task> beginOperation,
        string operationName,
        ulong userId,
        CancellationToken cancellationToken)
    {
        if (!TryAcquireOperationSlot())
        {
            throw new InvalidOperationException(
                "The bounded new-member Discord operation capacity is exhausted.");
        }

        Task operation;
        try
        {
            operation = beginOperation();
        }
        catch
        {
            ReleaseOperationSlot();
            throw;
        }

        var releaseOnReturn = true;
        try
        {
            await operation.WaitAsync(_runtimeOptions.OperationTimeout, cancellationToken);
        }
        catch
        {
            if (!operation.IsCompleted)
            {
                releaseOnReturn = false;
                ObserveLateCompletionAndRelease(operation, operationName, userId);
            }
            else
            {
                _ = operation.Exception;
            }

            throw;
        }
        finally
        {
            if (releaseOnReturn)
            {
                ReleaseOperationSlot();
            }
        }
    }

    private bool TryAcquireOperationSlot()
    {
        while (true)
        {
            var available = Volatile.Read(ref _availableOperationSlots);
            if (available <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _availableOperationSlots,
                    available - 1,
                    available) == available)
            {
                Interlocked.Increment(ref _activeOperationCount);
                return true;
            }
        }
    }

    private void ObserveLateCompletionAndRelease(Task operation, string operationName, ulong userId)
    {
        _ = operation.ContinueWith(
            completedTask =>
            {
                try
                {
                    if (completedTask.IsFaulted)
                    {
                        BeanBotLog.WelcomeDeliveryLateFailure(
                            _logger,
                            operationName,
                            userId,
                            completedTask.Exception!);
                    }
                }
                finally
                {
                    ReleaseOperationSlot();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseOperationSlot()
    {
        Interlocked.Decrement(ref _activeOperationCount);
        Interlocked.Increment(ref _availableOperationSlots);
    }
}
