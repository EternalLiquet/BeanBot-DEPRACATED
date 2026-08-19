using System.Diagnostics.CodeAnalysis;

namespace BeanBot.Services;

public interface IPunProvider
{
    bool TryGetRandomPun([NotNullWhen(true)] out string? pun);
}
