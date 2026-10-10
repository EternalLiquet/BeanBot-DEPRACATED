using BeanBot.Discord.Puns;
using BeanBot.Hosting;
using Discord;
using Discord.Interactions;

namespace BeanBot.Discord.Interactions;

public sealed class BeanBotInteractionModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string PunFallback = "The PunMaster is temporarily out of material.";
    private readonly IPunProvider _punProvider;
    private readonly InteractionExecutionContext _executionContext;

    public BeanBotInteractionModule(
        IPunProvider punProvider,
        InteractionExecutionContext executionContext)
    {
        _punProvider = punProvider ?? throw new ArgumentNullException(nameof(punProvider));
        _executionContext = executionContext
            ?? throw new ArgumentNullException(nameof(executionContext));
    }

    [SlashCommand("ping", "Check whether Bean Bot is responsive.", runMode: RunMode.Sync)]
    public Task PingAsync()
        => RespondReliablyAsync("Pong!", ephemeral: false);

    [SlashCommand("pun", "Get one PunMaster-branded pun.", runMode: RunMode.Sync)]
    public Task PunAsync()
        => RespondReliablyAsync(GetPunResponse(_punProvider), ephemeral: false);

    [SlashCommand("version", "Show Bean Bot's version.", runMode: RunMode.Sync)]
    public Task VersionAsync()
        => RespondReliablyAsync(
            FormatVersionResponse(BuildIdentity.Current.Version), ephemeral: true);

    [SlashCommand(
        "help",
        "Show Bean Bot's initial slash commands and legacy command syntax.",
        runMode: RunMode.Sync)]
    public Task HelpAsync()
        => RespondReliablyAsync(
            "Slash commands currently available: `/ping`, `/pun`, `/version`, `/help`, " +
            "`/role-menu create`, and `/role-menu delete`. " +
            "Role-menu setup commands require Manage Roles in a server. " +
            "Legacy message commands still work with `%`, `succ `, or by mentioning Bean Bot. " +
            "Use `%help` for the full legacy command list.",
            ephemeral: true);

    private async Task RespondReliablyAsync(string content, bool ephemeral)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _executionContext.CancellationToken);
        cancellation.CancelAfter(InteractionHandler.FailureResponseTimeout);
        var result = await InteractionInitialResponseWorkflow.ExecuteAsync(
            _executionContext,
            supportsOriginalResponse: true,
            InteractionHandler.FailureResponseTimeout,
            new InteractionInitialResponseOperations(
                operationToken => RespondAsync(
                    content,
                    ephemeral: ephemeral,
                    allowedMentions: AllowedMentions.None,
                    options: CreateRequestOptions(operationToken)),
                operationToken => ModifyOriginalResponseAsync(
                    properties => SetResponse(properties, content),
                    CreateRequestOptions(operationToken)),
                InteractionResponseErrors.IsKnownMissingOriginal),
            cancellation.Token);
        result.ThrowIfUnconfirmed();
    }

    private static void SetResponse(MessageProperties properties, string content)
    {
        properties.Content = content;
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

    internal static string GetPunResponse(IPunProvider punProvider)
    {
        ArgumentNullException.ThrowIfNull(punProvider);
        return punProvider.TryGetRandomPun(out var pun)
            ? pun
            : PunFallback;
    }

    internal static string FormatVersionResponse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || string.Equals(version.Trim(), "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "I can't tell which version I'm running.";
        }

        var buildVersion = version.Trim();
        return buildVersion == "0.0.0-local"
            ? "Development build"
            : $"BeanBot v{buildVersion}";
    }
}
