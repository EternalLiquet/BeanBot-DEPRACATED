using System.Reflection;
using BeanBot.Logging;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Interactions;

internal sealed class InteractionHandler : IDisposable
{
    internal static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(30);
    private const string SafeFailureMessage = "Bean Bot couldn't complete that command. Try again in a moment.";

    private readonly DiscordSocketClient _discordClient;
    private readonly InteractionService _interactionService;
    private readonly IServiceProvider _services;
    private readonly InteractionCommandRegistration _registration;
    private readonly ILogger<InteractionHandler> _logger;
    private bool _initialized;

    public InteractionHandler(
        DiscordSocketClient discordClient,
        InteractionService interactionService,
        IServiceProvider services,
        ILogger<InteractionHandler> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _interactionService = interactionService ?? throw new ArgumentNullException(nameof(interactionService));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _discordClient.InteractionCreated -= HandleInteractionAsync;
        _discordClient.Ready -= HandleReadyAsync;
        _initialized = false;
    }

    internal Task HandleReadyAsync()
        => RegisterCommandsSafelyAsync();

    internal async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        try
        {
            var context = new SocketInteractionContext(_discordClient, interaction);
            var result = await _interactionService.ExecuteCommandAsync(context, _services);
            if (result.IsSuccess)
            {
                return;
            }

            BeanBotLog.InteractionCommandFailed(_logger, interaction.Type, result.ErrorReason);
            await TryRespondWithFailureAsync(interaction);
        }
        catch (Exception exception)
        {
            BeanBotLog.InteractionCommandThrew(_logger, interaction.Type, exception);
            await TryRespondWithFailureAsync(interaction);
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

    private async Task TryRespondWithFailureAsync(SocketInteraction interaction)
    {
        try
        {
            if (interaction.HasResponded)
            {
                await interaction.FollowupAsync(SafeFailureMessage, ephemeral: true);
            }
            else
            {
                await interaction.RespondAsync(SafeFailureMessage, ephemeral: true);
            }
        }
        catch (Exception exception)
        {
            BeanBotLog.InteractionFailureResponseFailed(_logger, interaction.Type, exception);
        }
    }
}
