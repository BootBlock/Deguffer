namespace Deguffer.Core.Tests;

/// <summary>
/// This machine's own time zone, as a test that converts a local time has to see it.
///
/// <para>A conversion can be shown to have happened only at an instant the zone is offset from UTC,
/// and a zone that keeps summer time is offset for only part of the year. A fixed date therefore
/// proves the conversion on some machines, in some months, and passes identically without it
/// everywhere else. A test searches for its instant here instead, and asserts which kind of run it
/// got, so that a machine set to UTC passes visibly rather than silently.</para>
/// </summary>
internal static class LocalZone
{
    /// <summary>
    /// The first mid-month noon of <paramref name="year"/> at which this machine's zone is offset
    /// from UTC, or null on a machine that is set to UTC all year. Noon keeps the instant clear of
    /// the small hours, where a summer-time change makes a local time ambiguous.
    /// </summary>
    public static DateTime? OffsetInstant(int year)
    {
        for (var month = 1; month <= 12; month++)
        {
            var candidate = new DateTime(year, month, 15, 12, 0, 0, DateTimeKind.Utc);

            if (TimeZoneInfo.Local.GetUtcOffset(candidate) != TimeSpan.Zero)
            {
                return candidate;
            }
        }

        return null;
    }
}
