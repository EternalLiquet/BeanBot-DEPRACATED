namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuText
{
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
