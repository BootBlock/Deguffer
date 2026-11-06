namespace Deguffer.Core.Scanning;

/// <summary>
/// <see cref="Providers.DirectoryAge"/>'s rule over timestamps already read: the newest of a
/// directory's own time and its immediate entries', where each time is the newer of creation and last
/// write (<see cref="Safety.MinimumAge.NewestFileTimeOf(long, long)"/>). Every route that lists a
/// directory, or holds its entries in a table, answers the age through this, so the age column cannot
/// read one thing from the walk and another from the volume index.
///
/// <para><b>A time that was never read makes the whole age unknown.</b> Zero is the start of the NTFS
/// epoch, which is where the volume index lands a record whose <c>$STANDARD_INFORMATION</c> it could not
/// parse, and where a listing reports a time Windows never set. Taking the newest past a zero would be
/// right only when the zero was the oldest entry, and when it was not, the answer reads older than the
/// truth: the direction that invites a deletion.</para>
/// </summary>
internal static class OneLevelAge
{
    /// <summary>No age: the directory or one of its entries carries no time that was read.</summary>
    public const long Unknown = 0;

    /// <summary>The latest FILETIME a <see cref="DateTime"/> can hold.</summary>
    private static readonly long LatestFileTime = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>
    /// <paramref name="newest"/> raised to <paramref name="entry"/>, or <see cref="Unknown"/> where
    /// either is.
    /// </summary>
    public static long With(long newest, long entry) =>
        newest == Unknown || entry == Unknown ? Unknown : Math.Max(newest, entry);

    /// <summary>
    /// The age of the directory <paramref name="contents"/> lists, given its own time: every entry the
    /// listing holds, whatever a measurement then leaves out of its figure, because the age describes
    /// the directory rather than the part of it a removal takes.
    /// </summary>
    /// <param name="own">The newer of the directory's own creation and last-write times, as a FILETIME.</param>
    public static long Of(long own, in DirectoryContents contents)
    {
        var newest = own;

        newest = Raise(newest, contents.Entries);
        newest = Raise(newest, contents.Links);

        return Raise(newest, contents.ReparseFiles);
    }

    /// <summary>
    /// The age as the instant it names, or null where it is <see cref="Unknown"/> or beyond what a
    /// <see cref="DateTime"/> can hold. A garbled table record is a long, and converting one must
    /// not throw part-way through a plan.
    /// </summary>
    public static DateTime? ToUtc(long fileTime) =>
        fileTime is > Unknown && fileTime <= LatestFileTime ? DateTime.FromFileTimeUtc(fileTime) : null;

    private static long Raise(long newest, IReadOnlyList<WalkEntry> entries)
    {
        foreach (var entry in entries)
        {
            newest = With(newest, entry.NewestFileTime);
        }

        return newest;
    }
}
