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

    internal static string Percent(int percent) => $"{percent}%";

    /// <summary>
    /// A count of gigabytes in the unit a drive's document gives, where the document does not say whether a
    /// gigabyte is 10^9 or 2^30 bytes. <see cref="Size"/> states bytes in binary units, so the count cannot
    /// be passed through it without guessing the base.
    /// </summary>
    internal static string StatedGigabytes(UInt128 count) => $"{Count(count)} GB";

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
