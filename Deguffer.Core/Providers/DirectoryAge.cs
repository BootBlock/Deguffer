using System.IO.Enumeration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// When a directory was last written to, one level down, for §7's age column.
///
/// <para>One rule, because it is one fact. It was written three times before this — for build
/// output, for a declared log or dump folder, and for a VS Code workspace database — each time with
/// its own paragraph deriving the same reasoning, and the three had drifted into three different
/// answers. What each caller genuinely owns is <em>whether</em> its subject has a meaningful single
/// age at all, and that decision stays with the caller.</para>
///
/// <para><b>The newest of the directory's own timestamp and its immediate entries'.</b> Neither half
/// answers alone, and both halves fail in the direction that invites a deletion:</para>
///
/// <list type="bullet">
/// <item>NTFS moves a directory's own timestamp when an entry is added, removed or renamed, and
/// leaves it alone when an entry's contents change. So the directory alone reports the date its
/// layout last changed — the age of a project's first build, however often it is rebuilt into the
/// same set of files, and the age of a servicing log directory whose log is being appended to right
/// now.</item>
/// <item>The entries alone miss everything the directory catches. A build that prunes stale output
/// touches the directory and nothing that stays in it, so the answer is the age of what was left
/// behind. An emptied directory has no entries at all and answers "unknown", which §7 renders as a
/// blank column — a project cleaned an hour ago made indistinguishable from one that could not be
/// read.</item>
/// </list>
///
/// <para><b>One level, deliberately.</b> This runs per project across a whole source root, and the
/// column exists to tell a year-old project from this morning's. That resolution does not justify
/// enumerating hundreds of thousands of files to sharpen, and for build output it would mean walking
/// the very tree discovery went out of its way not to walk. Where a location's content nests below
/// this level, the answer is not to walk deeper but to report no age — see
/// <see cref="DeclaredLocation.ReportsAge"/> for a Maven repository, whose top level moves only when
/// a whole new group first appears.</para>
///
/// <para><b>Each time is the newer of creation and last write</b>, the one number
/// <see cref="MinimumAge"/> judges a file by, for the reason it gives: a file copied or extracted into
/// a cache keeps its source's last write, which can be years old, while its creation here is now. Last
/// write alone would date a package unpacked this morning by the day it was published, which reads
/// older than the truth. One rule for both also keeps the column from calling a row months old while
/// the guard withholds its files as recent, and it is the number the volume index already keeps for
/// every record, so a measurement read from the index dates a directory exactly as a walk does
/// (<see cref="ScanResult.LastWritten"/>, <see cref="OneLevelAge"/>).</para>
///
/// <para><b>Not the source beside it either</b>, which is the other tempting answer for build
/// output. An age is asked in order to price a deletion, and what a deletion costs is a rebuild — so
/// "when was this last built" is the question and "when was this project last edited" is a different
/// one.</para>
/// </summary>
internal static class DirectoryAge
{
    /// <summary>
    /// Each entry's time as a FILETIME, read from the listing's own record of the entry. A
    /// <see cref="FileSystemInfo"/> an entry would cost its full path and name strings, for one
    /// timestamp, and a <c>node_modules</c> holds thousands of entries at its top level.
    /// </summary>
    private static readonly FileSystemEnumerable<long>.FindTransform NewestFileTime =
        static (ref FileSystemEntry entry) =>
            MinimumAge.NewestFileTimeOf(entry.CreationTimeUtc.UtcDateTime, entry.LastWriteTimeUtc.UtcDateTime);

    /// <summary>
    /// The age of <paramref name="directory"/>, or null where it is absent, refused, or could not be
    /// listed to the end.
    ///
    /// <para>For a caller that decides by the age before anything is measured. A target whose age is
    /// only shown is dated by its measurement instead (<see cref="DeletionTarget.DatedOneLevelDown"/>),
    /// which reads the directory's own time by the same attribute read and its entries' from the same
    /// listing, so the two answer alike.</para>
    /// </summary>
    public static DateTime? Of(string directory, CancellationToken ct = default)
    {
        // A missing path has no time to read, and a time made up for one would be the Windows epoch:
        // January 1601 in the age column is the oldest invitation there is to delete something that
        // has already gone.
        if (LongPath.ProbeDirectoryTime(directory, out var newest) is not PathPresence.Present)
        {
            return null;
        }

        using var listing = new DirectoryListing<long>(directory, NewestFileTime);

        while (listing.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            newest = OneLevelAge.With(newest, listing.Current);
        }

        // §5.3 makes a refusal ordinary, but it is not an answer. The rule is the newest of the
        // directory and its entries, and a listing refused or ended part-way leaves entries unread —
        // the half that reads *older* than the truth, which is the half that invites a deletion. A
        // servicing log directory the account may not list is the live case: WMI rewrites the traces
        // inside it while the parent's timestamp sits still, and Tier 3 shows that age beside a row
        // whose loss is permanent. So §7's column goes blank, for the reason
        // DeclaredLocation.ReportsAge gives for a location that cannot be dated one level down.
        return listing.Outcome is PathPresence.Present ? OneLevelAge.ToUtc(newest) : null;
    }

    /// <summary>
    /// The age of each of <paramref name="directories"/>, by <see cref="Of(string, CancellationToken)"/>,
    /// in their order.
    ///
    /// <para><b>Several at once, bounded by the disk's own number</b> (<see cref="DiskBoundWork"/>). A
    /// row that decides by age dates every candidate before it measures any, a Claude Code row its
    /// session folders among them, and each date is a directory opened and listed, which waits on the
    /// disk. One after another, a row paid every wait in turn. An async caller starts it on
    /// <see cref="WorkerThreads.RunAsync{T}(Func{T}, CancellationToken)"/>.</para>
    /// </summary>
    /// <param name="tuning">What the walks of each volume run with. See <see cref="IDirectoryScanner.Tuning"/>.</param>
    public static IReadOnlyList<DateTime?> OfEach(IReadOnlyList<string> directories, ScanTuner tuning, CancellationToken ct) =>
        OfEach(directories, tuning, ListingBudget.Shared, ct);

    /// <param name="budget">The budget every listing takes its slot in: the seam a test holds a volume's slots through.</param>
    internal static IReadOnlyList<DateTime?> OfEach(
        IReadOnlyList<string> directories,
        ScanTuner tuning,
        ListingBudget budget,
        CancellationToken ct) =>
        DiskBoundWork.Each(directories, tuning, budget, directory => Of(directory, ct), ct);

    /// <summary>
    /// The same rule, for a caller whose listings have read both halves already: the directory's own
    /// time from the listing of its parent, and its entries' from its own listing.
    /// </summary>
    /// <param name="own">
    /// The newer of the directory's own creation and last-write times as a FILETIME, as
    /// <see cref="MinimumAge.NewestFileTimeOf(FileSystemInfo)"/> gives it.
    /// </param>
    /// <param name="entries">The same time of each of its immediate entries, from one whole listing.</param>
    /// <returns>The age, or null where any time was never set. See <see cref="OneLevelAge"/>.</returns>
    public static DateTime? Of(long own, IEnumerable<long> entries) =>
        OneLevelAge.ToUtc(entries.Aggregate(own, OneLevelAge.With));
}
