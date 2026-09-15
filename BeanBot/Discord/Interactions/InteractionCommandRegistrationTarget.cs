namespace BeanBot.Discord.Interactions;

internal sealed class InteractionCommandRegistrationTarget
{
    private InteractionCommandRegistrationTarget(ulong? guildId)
    {
        GuildId = guildId;
    }

    internal static InteractionCommandRegistrationTarget Global { get; } = new(null);

    internal ulong? GuildId { get; }

    internal bool IsGlobal => GuildId is null;

    internal string ScopeName => IsGlobal ? "Global" : "Guild";

    internal static InteractionCommandRegistrationTarget FromGuildId(ulong? guildId)
        => guildId is ulong value ? ForGuild(value) : Global;

    internal static InteractionCommandRegistrationTarget ForGuild(ulong guildId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(guildId);
        return new InteractionCommandRegistrationTarget(guildId);
    }

    internal Task RegisterAsync(
        Func<bool, Task> registerGlobal,
        Func<ulong, bool, Task> registerGuild)
    {
        ArgumentNullException.ThrowIfNull(registerGlobal);
        ArgumentNullException.ThrowIfNull(registerGuild);

        return GuildId is ulong guildId
            ? registerGuild(guildId, true)
            : registerGlobal(true);
    }
}
