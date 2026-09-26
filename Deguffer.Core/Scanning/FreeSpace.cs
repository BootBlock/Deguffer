namespace Deguffer.Core.Scanning;

/// <summary>
/// §7: show free space before and after, prominently. It is the only number the user came for.
///
/// <para>This states a size in words. The figures themselves are read through
/// <see cref="Safety.IVolumeInventory.SpaceOf"/>, which a test can answer for a volume it built.</para>
/// </summary>
public static class FreeSpace
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// A size in words, qualified where it is a prediction rather than a measurement.
    ///
    /// Both of §5.5's routes measure exactly and agree, so neither is hedged. What is hedged is a
    /// figure a tool produced about its own future behaviour — conda's dry run — and a sole-link
    /// sum whose link counts move whenever a project installs a dependency. Saying "about" is the
    /// difference between reporting a measurement and repeating someone else's forecast.
    ///
    /// <para>Where no bytes go and entries do, the entries are the figure. A leftover of empty folders
    /// frees nothing measurable, and "0 B" beside a row that is ready to clean reads as nothing to do.
    /// Bytes stay the figure wherever there are any, so the count never becomes a second number in
    /// every label.</para>
    /// </summary>
    public static string Format(ScanSize size) => size switch
    {
        { Reclaimable: 0, Entries: > 0 } => Items(size.Entries),
        { IsApproximate: true } => $"about {Format(size.Reclaimable)}",
        _ => Format(size.Reclaimable),
    };

    private static string Items(long entries) => entries == 1 ? "1 item" : $"{entries:N0} items";

    /// <summary>Human-readable size, in the binary units Windows itself reports.</summary>
    public static string Format(long bytes)
    {
        double value = Math.Abs(bytes);
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var sign = bytes < 0 ? "-" : string.Empty;
        var precision = unit >= 2 && value < 100 ? 1 : 0;

        return $"{sign}{value.ToString($"F{precision}")} {Units[unit]}";
    }
}
