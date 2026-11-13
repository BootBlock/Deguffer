using Deguffer.Core.Scanning;

namespace Deguffer.Core.Health;

/// <summary>Drive figures in words, so every reading is stated the same way.</summary>
internal static class HealthText
{
    private const int HoursPerDay = 24;
    private const int DaysPerYear = 365;
    private const int KelvinOffset = 273;

    internal static int Celsius(int kelvin) => kelvin - KelvinOffset;

    internal static string Temperature(int celsius) => $"{celsius} °C";

    internal static string Count(UInt128 count) => count.ToString("N0", null);

    /// <remarks>A <see langword="long"/>, so that a raw value read from up to six bytes is stated whole.</remarks>
    internal static string Percent(long percent) => $"{percent}%";

    internal static string Gigabytes(UInt128 gigabytes) => $"{Count(gigabytes)} GB";

    /// <summary>A size, held at the largest the shared formatter can state rather than wrapped into nonsense.</summary>
    internal static string Size(UInt128 bytes) => FreeSpace.Format(bytes > long.MaxValue ? long.MaxValue : (long)bytes);

    /// <summary>A run of hours, with the length of time it comes to.</summary>
    internal static string Hours(UInt128 hours)
    {
        var total = hours > int.MaxValue ? int.MaxValue : (int)hours;
        var days = total / HoursPerDay;

        if (days == 0)
        {
            return $"{Count(hours)} hours";
        }

        var years = days / DaysPerYear;
        var span = years > 0 ? $"{Plural(years, "year")} {Plural(days % DaysPerYear, "day")}" : Plural(days, "day");

        return $"{Count(hours)} hours ({span})";
    }

    private static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}
