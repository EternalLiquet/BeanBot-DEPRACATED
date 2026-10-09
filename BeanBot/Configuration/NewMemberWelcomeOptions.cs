namespace BeanBot.Configuration;

internal sealed class NewMemberWelcomeOptions
{
    internal const int DiscordMessageMaximumLength = 2000;

    internal const string DefaultMessage =
        "Please read the rules in the Eli's Charter channel. If you agree to these rules and are over the age of 17, please DM one of the moderators with the blue role \"Student Council\" (i.e discount Hatate/Makoto Kikuchi#2351) for full access to the server! (I promise it's worth it)";

    internal NewMemberWelcomeOptions(bool enabled, string message)
    {
        Enabled = enabled;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    internal bool Enabled { get; }
    internal string Message { get; }

    internal static NewMemberWelcomeOptions Create(BeanBotNewMemberWelcomeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var enabled = settings.Enabled is null || bool.Parse(settings.Enabled);
        return new NewMemberWelcomeOptions(
            enabled,
            settings.Message ?? DefaultMessage);
    }
}
