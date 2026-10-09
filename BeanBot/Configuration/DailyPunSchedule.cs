using System.Globalization;

namespace BeanBot.Configuration;

public sealed class DailyPunSchedule
{
    public const string DefaultTime = "16:20";
    public const string DefaultTimeZoneId = "America/Chicago";

    internal DailyPunSchedule(TimeSpan localTime, string timeZoneId, TimeZoneInfo timeZone)
    {
        LocalTime = localTime;
        TimeZoneId = timeZoneId;
        TimeZone = timeZone;
    }

    public TimeSpan LocalTime { get; }
    public string TimeZoneId { get; }
    public TimeZoneInfo TimeZone { get; }

    internal static DailyPunSchedule Create(BeanBotDailyPunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!TryParseLocalTime(settings.Time, out var localTime) ||
            !TryResolveTimeZone(settings.TimeZone, out var timeZone))
        {
            throw new InvalidOperationException(
                "Daily pun settings must be validated before creating BeanBot options.");
        }

        return new DailyPunSchedule(localTime, settings.TimeZone!, timeZone!);
    }

    internal static DailyPunSchedule CreateDefault()
        => Create(new BeanBotDailyPunSettings());

    internal static bool TryParseLocalTime(string? value, out TimeSpan localTime)
    {
        if (TimeOnly.TryParseExact(
            value,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed))
        {
            localTime = parsed.ToTimeSpan();
            return true;
        }

        localTime = default;
        return false;
    }

    internal static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            timeZone = null;
            return false;
        }

        if (TryFindTimeZone(timeZoneId, out timeZone))
        {
            return true;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId) &&
            TryFindTimeZone(windowsId, out timeZone))
        {
            return true;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId) &&
            TryFindTimeZone(ianaId, out timeZone))
        {
            return true;
        }

        timeZone = null;
        return false;
    }

    private static bool TryFindTimeZone(string timeZoneId, out TimeZoneInfo? timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = null;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = null;
            return false;
        }
    }
}
