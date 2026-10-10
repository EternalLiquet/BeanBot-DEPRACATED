using System.Globalization;

namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuText
{
    // Discord renders these as blank space even though .NET doesn't treat them as whitespace.
    private static readonly HashSet<char> BlankLookingCharacters =
        ['\u115F', '\u1160', '\u2800', '\u3164', '\uFFA0'];

    internal static bool HasVisibleText(string? value)
        => value is not null
           && value.Any(character => !char.IsWhiteSpace(character)
                                     && !BlankLookingCharacters.Contains(character)
                                     && char.GetUnicodeCategory(character) is not
                                         (UnicodeCategory.Format
                                         or UnicodeCategory.Control
                                         or UnicodeCategory.NonSpacingMark
                                         or UnicodeCategory.EnclosingMark));

    internal static string TruncateWithEllipsis(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var cutoff = maximumLength - 1;
        if (cutoff > 0 && char.IsHighSurrogate(value[cutoff - 1]))
        {
            cutoff--;
        }

        return value[..cutoff] + "…";
    }
}
