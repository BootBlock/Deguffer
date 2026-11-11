namespace Deguffer.Core.Health;

/// <summary>The page's one line about all the drives together.</summary>
public static class DriveOverview
{
    /// <summary>
    /// A drive of concern is named as soon as it is known. "No concerns" is said only once every
    /// drive has given its own overall judgement, because a drive still being read, or one with no
    /// report at all, has not been found to be free of them.
    /// </summary>
    /// <param name="drives">How many drives are listed.</param>
    /// <param name="read">The latest report of each drive read so far.</param>
    public static string Headline(int drives, IReadOnlyCollection<DriveHealth> read)
    {
        if (drives == 0)
        {
            return "No drives found.";
        }

        var total = Count(drives, "drive");
        var concerns = read.Count(report => report.Verdict.IsConcern());
        var withoutReport = read.Count(report => report.Verdict is DriveVerdict.Unknown);

        if (concerns > 0)
        {
            return $"{total}, {concerns} {(concerns == 1 ? "needs" : "need")} attention.";
        }

        if (read.Count < drives)
        {
            return $"Reading {total}…";
        }

        return withoutReport > 0
            ? $"{total}, {withoutReport} with no health report."
            : $"{total}, no concerns reported.";
    }

    private static string Count(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}
