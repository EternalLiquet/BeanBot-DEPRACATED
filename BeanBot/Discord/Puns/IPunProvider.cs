using System.Diagnostics.CodeAnalysis;

namespace BeanBot.Discord.Puns;

public interface IPunProvider
{
    bool TryGetRandomPun([NotNullWhen(true)] out string? pun);
}
