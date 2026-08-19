using System.Diagnostics.CodeAnalysis;

namespace BeanBot.Discord.Commands;

public interface IPunProvider
{
    bool TryGetRandomPun([NotNullWhen(true)] out string? pun);
}
