namespace Deguffer.Core.Scanning;

/// <summary>
/// §7: "last touched 5 months ago" — the age column's sentence. Lives in Core beside
/// <see cref="FreeSpace"/> for the same reason: it is a rule about what the user is told, and
/// keeping it out of the shell is what makes it provable without a WinUI host.
///
/// The absent case is the one that carries risk. A missing timestamp reads as <c>Unknown</c> and
/// never as an age, because an age is what invites the user to delete something — "we could not
/// tell" must never be presented as "nobody has touched this in a year".
/// </summary>
public static class RelativeAge
{
    /// <summary>The label for <paramref name="when"/>, or <c>Unknown</c> if there is no timestamp.</summary>
    /// <param name="now">Injected so the description is testable without a clock.</param>
    public static string Describe(DateTime? when, DateTime now)
    {
        if (when is not { } timestamp)
        {
            return "Unknown";
        }

        // A file written during the scan, or a clock that disagrees with the filesystem's, both
        // produce a timestamp in the future. Neither is an age, and rounding it to "Today" is the
        // only reading that is both honest and safe.
        var days = (int)(now.ToUniversalTime() - timestamp.ToUniversalTime()).TotalDays;

        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => $"{days} days ago",
            < 31 => Plural(days / 7, "week"),
            // 30, not the 30.44-day average: this is a label a reader scans, not an interval to
            // compute with, and §7's own example — 150 days as "5 months ago" — is the rounding a
            // reader expects. Dividing by 31 would report that same file as four months old.
            < 365 => Plural(days / 30, "month"),
            _ => Plural(days / 365, "year"),
        };
    }

    private static string Plural(int count, string unit) =>
        count == 1 ? $"1 {unit} ago" : $"{count} {unit}s ago";
}
