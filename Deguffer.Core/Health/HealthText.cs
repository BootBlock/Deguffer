using Deguffer.Core.Scanning;
using Deguffer.Core.Text;

namespace Deguffer.Core.Health;

/// <summary>Drive figures in words, so every reading is stated the same way.</summary>
internal static class HealthText
{
    private const int MinutesPerHour = 60;
    private const int HoursPerDay = 24;
    private const int DaysPerYear = 365;
    private const int KelvinOffset = 273;

    internal static int Celsius(int kelvin) => kelvin - KelvinOffset;

    internal static string Temperature(int celsius) => $"{celsius} °C";

    internal static string Count(UInt128 count) => count.ToString("N0", null);

    internal static string Percent(int percent) => $"{percent}%";

    internal static string Size(UInt128 bytes) => FreeSpace.FormatWide(bytes);

    /// <summary>A run of hours, with the length of time it comes to.</summary>
    internal static string Hours(UInt128 hours) =>
        Days(hours) is { } span ? $"{Count(hours)} hours ({span})" : $"{Count(hours)} hours";

    /// <summary>A run of minutes, with the length of time it comes to.</summary>
    internal static string Minutes(UInt128 minutes)
    {
        var hours = minutes / MinutesPerHour;
        var span = Days(hours) ?? (hours > 0 ? Counted.Of(hours, "hour", "hours") : null);

        return span is null ? Counted.Of(minutes, "minute", "minutes") : $"{Count(minutes)} minutes ({span})";
    }

    /// <summary>The days and years a run of hours comes to, or null where it is less than a day.</summary>
    private static string? Days(UInt128 hours)
    {
        var total = hours > int.MaxValue ? int.MaxValue : (int)hours;
        var days = total / HoursPerDay;

        if (days == 0)
        {
            return null;
        }

        var years = days / DaysPerYear;

        return years > 0
            ? $"{Counted.Of((UInt128)years, "year", "years")} {Counted.Of((UInt128)(days % DaysPerYear), "day", "days")}"
            : Counted.Of((UInt128)days, "day", "days");
    }
}
