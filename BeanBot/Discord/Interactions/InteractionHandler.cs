using System.Reflection;
using BeanBot.Logging;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Interactions;

internal sealed class InteractionHandler : IAsyncDisposable
{
    internal static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FailureResponseTimeout = TimeSpan.FromSeconds(3);
    internal const int MaximumConcurrentOperations = 64;
    internal const int MaximumConcurrentBusyResponses = 4;
    private const string SafeFailureMessage = "Bean Bot couldn't complete that command. Try again in a moment.";
    private const string BusyMessage = "Bean Bot is busy right now. Try again in a moment.";

    private readonly DiscordSocketClient _discordClient;
    private readonly InteractionService _interactionService;
    private readonly IServiceProvider _services;
    private readonly InteractionCommandRegistration _registration;
    private readonly InteractionExecutionContext _executionContext;
    private readonly InteractionOperationTracker _operationTracker;
    private readonly InteractionOperationTracker _busyResponseTracker;
    private readonly ILogger<InteractionHandler> _logger;
    private bool _initialized;

    public InteractionHandler(
        DiscordSocketClient discordClient,
        InteractionService interactionService,
        IServiceProvider services,
        InteractionExecutionContext executionContext,
        IHostApplicationLifetime applicationLifetime,
        ILogger<InteractionHandler> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _interactionService = interactionService ?? throw new ArgumentNullException(nameof(interactionService));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _executionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _operationTracker = new InteractionOperationTracker(
            ShutdownDrainTimeout,
            _logger,
            MaximumConcurrentOperations,
            applicationLifetime.ApplicationStopping);
        _busyResponseTracker = new InteractionOperationTracker(
            ShutdownDrainTimeout,
            _logger,
            MaximumConcurrentBusyResponses,
            applicationLifetime.ApplicationStopping);
        _registration = new InteractionCommandRegistration(
            () => _interactionService.RegisterCommandsGloballyAsync(deleteMissing: true),
            RegistrationTimeout);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized)
        {
            return;
        }

        await _interactionService.AddModulesAsync(
            Assembly.GetEntryAssembly() ?? typeof(InteractionHandler).Assembly,
            _services);

        _discordClient.InteractionCreated += HandleInteractionAsync;
        _discordClient.Ready += HandleReadyAsync;
        _initialized = true;

        if (_discordClient.ConnectionState == ConnectionState.Connected)
        {
            await RegisterCommandsSafelyAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            _discordClient.InteractionCreated -= HandleInteractionAsync;
            _discordClient.Ready -= HandleReadyAsync;
            _initialized = false;
        }

        await Task.WhenAll(
            _operationTracker.DisposeAsync().AsTask(),
            _busyResponseTracker.DisposeAsync().AsTask());
    }

    internal Task HandleReadyAsync()
        => RegisterCommandsSafelyAsync();

    internal Task HandleInteractionAsync(SocketInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var admission = _operationTracker.Start(cancellationToken =>
            ExecuteInteractionAsync(interaction, cancellationToken));
        if (admission == InteractionOperationAdmission.Saturated)
        {
            _busyResponseTracker.Start(cancellationToken =>
                TryRespondWithBusyAsync(interaction, cancellationToken));
        }

        return Task.CompletedTask;
    }

    private async Task ExecuteInteractionAsync(
        SocketInteraction interaction,
        CancellationToken cancellationToken)
    {
        using var executionScope = _executionContext.Enter(cancellationToken);
        try
        {
            var context = new SocketInteractionContext(_discordClient, interaction);
            var result = await _interactionService.ExecuteCommandAsync(context, _services);
            if (result.IsSuccess)
            {
                return;
            }

            BeanBotLog.InteractionCommandFailed(_logger, interaction.Type, result.ErrorReason);
            await TryRespondWithFailureAsync(interaction, cancellationToken);
        }
        catch (Exception exception)
            when (cancellationToken.IsCancellationRequested
                  && IsCancellationException(exception))
        {
            BeanBotLog.InteractionCanceledForShutdown(_logger, interaction.Type);
        }
        catch (Exception exception)
        {
            BeanBotLog.InteractionCommandThrew(_logger, interaction.Type, exception);
            await TryRespondWithFailureAsync(interaction, cancellationToken);
        }
    }

    private async Task RegisterCommandsSafelyAsync()
    {
        try
        {
            if (await _registration.EnsureRegisteredAsync())
            {
                BeanBotLog.InteractionCommandsRegistered(_logger);
            }
        }
        catch (Exception exception)
        {
            BeanBotLog.InteractionCommandRegistrationFailed(_logger, exception);
        }
    }

    private async Task TryRespondWithFailureAsync(
        SocketInteraction interaction,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        using var responseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        responseCancellation.CancelAfter(FailureResponseTimeout);
        try
        {
            var result = await InteractionFailureResponseWorkflow.ExecuteAsync(
                _executionContext.InitialResponse,
                interaction.HasResponded,
                new InteractionFailureResponseOperations(
                    operationToken => interaction.ModifyOriginalResponseAsync(
                        SetFailureResponse,
                        CreateRequestOptions(operationToken)),
                    operationToken => interaction.RespondAsync(
                        SafeFailureMessage,
                        ephemeral: true,
                        allowedMentions: AllowedMentions.None,
                        options: CreateRequestOptions(operationToken)),
                    InteractionResponseErrors.IsKnownMissingOriginal),
                responseCancellation.Token);
            var responseException = result.Exception;
            if (responseException is null)
            {
                return;
            }

            if (responseException is OperationCanceledException
                && responseCancellation.IsCancellationRequested)
            {
                BeanBotLog.InteractionFailureResponseCanceled(_logger, interaction.Type);
                return;
            }

            BeanBotLog.InteractionFailureResponseFailed(
                _logger,
                interaction.Type,
                responseException);
        }
        catch (OperationCanceledException) when (responseCancellation.IsCancellationRequested)
        {
            BeanBotLog.InteractionFailureResponseCanceled(_logger, interaction.Type);
        }
        catch (Exception exception)
        {
            BeanBotLog.InteractionFailureResponseFailed(_logger, interaction.Type, exception);
        }
    }

    private async Task TryRespondWithBusyAsync(
        SocketInteraction interaction,
        CancellationToken cancellationToken)
    {
        using var responseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        responseCancellation.CancelAfter(FailureResponseTimeout);
        try
        {
            await interaction.RespondAsync(
                BusyMessage,
                ephemeral: true,
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions
                {
                    CancelToken = responseCancellation.Token
                });
        }
        catch (OperationCanceledException) when (responseCancellation.IsCancellationRequested)
        {
            BeanBotLog.InteractionFailureResponseCanceled(_logger, interaction.Type);
        }
        catch (Exception exception)
        {
            BeanBotLog.InteractionFailureResponseFailed(_logger, interaction.Type, exception);
        }
    }

    internal static void SetFailureResponse(MessageProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        properties.Content = SafeFailureMessage;
        Embed[] embeds = [];
        properties.Embeds = embeds;
        properties.Components = MessageComponent.Empty;
        properties.AllowedMentions = AllowedMentions.None;
    }

    private static RequestOptions CreateRequestOptions(CancellationToken cancellationToken)
        => new()
        {
            CancelToken = cancellationToken
        };

    internal static bool IsCancellationException(Exception exception)
        => exception is OperationCanceledException
           || (exception is AggregateException aggregate
               && aggregate.Flatten().InnerExceptions.Any(
                   innerException => innerException is OperationCanceledException));
}
