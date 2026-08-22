using BeanBot.Discord.Commands;
using Discord.Interactions;

namespace BeanBot.Discord.Interactions;

public sealed class BeanBotInteractionModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string PunFallback = "The PunMaster is temporarily out of material.";
    private readonly IPunProvider _punProvider;

    public BeanBotInteractionModule(IPunProvider punProvider)
    {
        _punProvider = punProvider ?? throw new ArgumentNullException(nameof(punProvider));
    }

    [SlashCommand("ping", "Check whether Bean Bot is responsive.")]
    public Task PingAsync()
        => RespondAsync("Pong!");

    [SlashCommand("pun", "Get one PunMaster-branded pun.")]
    public Task PunAsync()
        => RespondAsync(GetPunResponse(_punProvider));

    [SlashCommand("help", "Show Bean Bot's initial slash commands and legacy command syntax.")]
    public Task HelpAsync()
        => RespondAsync(
            "Slash commands currently available: `/ping`, `/pun`, `/help`, " +
            "`/role-menu create`, and `/role-menu delete`. " +
            "Role-menu setup commands require Manage Roles in a server. " +
            "Legacy message commands still work with `%`, `succ `, or by mentioning Bean Bot. " +
            "Use `%help` for the full legacy command list.",
            ephemeral: true);

    internal static string GetPunResponse(IPunProvider punProvider)
    {
        ArgumentNullException.ThrowIfNull(punProvider);
        return punProvider.TryGetRandomPun(out var pun)
            ? pun
            : PunFallback;
    }
}
