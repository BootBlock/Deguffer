namespace Deguffer.Core.Exploring;

/// <summary>
/// One filesystem timestamp, as whole minutes since the start of 1601, in four bytes.
///
/// <para>The compression is the reason this type exists rather than a <see cref="DateTime"/>. A
/// timestamp pair per node across a 2.4M-record volume is not free, and
/// <see cref="ExploreTree"/> is structure-of-arrays for exactly that reason: two
/// <see cref="DateTime"/> arrays cost 38 MB on such a volume where two of these cost 19 MB, and
/// the file-table route sizes its arrays to the whole record count before it reads a single one, so
/// that cost is paid in full whether or not the slots are used.</para>
///
/// <para>What is given up is seconds, and nothing else. Both consumers are a date a person reads —
/// an age column and a colour band — and neither can express a second. The two scan routes gain
/// from it as well: the file table reports 100-nanosecond ticks and a directory walk reports a
/// <see cref="DateTime"/>, and truncating both to the minute is what makes them state the same
/// instant identically rather than nearly.</para>
///
/// <para>1601 rather than the Unix epoch because that is where NTFS counts from, so the common
/// conversion is a division and the zero value needs no translating. Zero <em>is</em> the unknown
/// value: NTFS writes it for a timestamp it never set, and no real file carries it.</para>
/// </summary>
/// <param name="MinutesSinceWindowsEpoch">
/// Whole minutes since 1601-01-01 UTC, or zero where nothing established a time. An
/// <see cref="int"/> reaches the year 5684, so it will not run out before the format does.
/// </param>
public readonly record struct ExploreTimestamp(int MinutesSinceWindowsEpoch)
    : IComparable<ExploreTimestamp>
{
    private static readonly long WindowsEpochTicks =
        new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>
    /// No time was established. Distinct from any date, and it must stay that way: an age is what
    /// invites a user to act on something, so "we could not tell" must never read as "nobody has
    /// touched this in a year". <see cref="Scanning.RelativeAge"/> holds the same rule for the
    /// sentence this ends up as.
    /// </summary>
    public static ExploreTimestamp Unknown => default;

    public bool IsKnown => MinutesSinceWindowsEpoch > 0;

    /// <summary>The instant this stands for, or null where nothing established one.</summary>
    public DateTime? Utc => IsKnown
        ? new DateTime(
            WindowsEpochTicks + (MinutesSinceWindowsEpoch * TimeSpan.TicksPerMinute),
            DateTimeKind.Utc)
        : null;

    /// <summary>
    /// A Windows <c>FILETIME</c> — 100-nanosecond intervals since 1601 — as the master file table
    /// stores one in <c>$STANDARD_INFORMATION</c>.
    ///
    /// <para>A tick is that same 100 nanoseconds, so the conversion is one division and no epoch
    /// arithmetic at all. Anything that cannot be one of these minutes is unknown rather than
    /// clamped: a negative value is a corrupt record, and one past the <see cref="int"/> range is a
    /// date beyond the year 5684, and inventing a plausible date from either is worse than saying
    /// nothing.</para>
    /// </summary>
    public static ExploreTimestamp FromFileTime(long fileTime) =>
        FromTicksSinceEpoch(fileTime);

    /// <summary>
    /// A <see cref="DateTime"/> as the directory walk has it, from
    /// <see cref="FileSystemInfo.CreationTimeUtc"/> and its neighbours.
    ///
    /// <para>Converted to UTC first rather than assumed to be in it. The properties the walk reads
    /// are already UTC and this changes nothing for them, but a local time silently reinterpreted
    /// as UTC is an error that shows up as an hour's drift twice a year and never as a
    /// failure.</para>
    /// </summary>
    public static ExploreTimestamp FromUtc(DateTime utc) =>
        FromTicksSinceEpoch(utc.ToUniversalTime().Ticks - WindowsEpochTicks);

    /// <summary>The later of two, which is what a subtree's newest write is rolled up with.</summary>
    public static ExploreTimestamp Newer(ExploreTimestamp left, ExploreTimestamp right) =>
        left.MinutesSinceWindowsEpoch >= right.MinutesSinceWindowsEpoch ? left : right;

    public int CompareTo(ExploreTimestamp other) =>
        MinutesSinceWindowsEpoch.CompareTo(other.MinutesSinceWindowsEpoch);

    private static ExploreTimestamp FromTicksSinceEpoch(long ticks)
    {
        if (ticks <= 0)
        {
            return Unknown;
        }

        var minutes = ticks / TimeSpan.TicksPerMinute;

        return minutes is > 0 and <= int.MaxValue ? new ExploreTimestamp((int)minutes) : Unknown;
    }
}
