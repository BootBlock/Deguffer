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
/// <para><b>Not the source beside it either</b>, which is the other tempting answer for build
/// output. An age is asked in order to price a deletion, and what a deletion costs is a rebuild — so
/// "when was this last built" is the question and "when was this project last edited" is a different
/// one.</para>
/// </summary>
internal static class DirectoryAge
{
    /// <summary>
    /// Each entry's last write as ticks, read from the listing's own record of the entry. A
    /// <see cref="FileSystemInfo"/> an entry would cost its full path and name strings, for one
    /// timestamp, and a <c>node_modules</c> holds thousands of entries at its top level.
    /// </summary>
    private static readonly FileSystemEnumerable<long>.FindTransform LastWriteTicks =
        static (ref FileSystemEntry entry) => entry.LastWriteTimeUtc.UtcTicks;

    /// <summary>
    /// The age of <paramref name="directory"/>, or null where it is absent, refused, or could not be
    /// listed to the end.
    /// </summary>
    public static DateTime? Of(string directory, CancellationToken ct = default)
    {
        var info = new DirectoryInfo(LongPath.Extended(directory));

        if (!info.Exists)
        {
            // LastWriteTimeUtc answers for a missing path with the start of the Windows epoch rather
            // than failing, and January 1601 in the age column is the oldest invitation there is to
            // delete something that has already gone.
            return null;
        }

        // Read by the attribute read that answered Exists, so it costs nothing more.
        var newest = info.LastWriteTimeUtc.Ticks;

        using var listing = new DirectoryListing<long>(directory, LastWriteTicks);

        while (listing.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            newest = Math.Max(newest, listing.Current);
        }

        // §5.3 makes a refusal ordinary, but it is not an answer. The rule is the newest of the
        // directory and its entries, and a listing refused or ended part-way leaves entries unread —
        // the half that reads *older* than the truth, which is the half that invites a deletion. A
        // servicing log directory the account may not list is the live case: WMI rewrites the traces
        // inside it while the parent's timestamp sits still, and Tier 3 shows that age beside a row
        // whose loss is permanent. So §7's column goes blank, for the reason
        // DeclaredLocation.ReportsAge gives for a location that cannot be dated one level down.
        return listing.Outcome is PathPresence.Present ? new DateTime(newest, DateTimeKind.Utc) : null;
    }

    /// <summary>
    /// The age of each of <paramref name="directories"/>, by <see cref="Of(string, CancellationToken)"/>,
    /// in their order.
    ///
    /// <para><b>Several at once, bounded by the disk's own number</b> (G4). One row dates hundreds of
    /// targets — a source root's <c>obj</c> folders, its <c>node_modules</c> — and each date is a
    /// directory opened and listed, which waits on the disk. One after another, a row paid every wait
    /// in turn. Here the list is worked by as many threads as a walk of the busiest volume asked about
    /// runs (<see cref="WalkTuning.Threads"/>), and each listing takes a slot in
    /// <see cref="ListingBudget.Shared"/> under its own volume's number, so dating shares a disk with
    /// the walks of the rows planned beside it rather than adding to them. A spinning disk tuned to one
    /// thread is still read one directory at a time.</para>
    ///
    /// <para>On threads of the list's own rather than the pool's (<see cref="ListWorkers"/>), with the
    /// calling thread working too. An async caller starts it on
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
        CancellationToken ct)
    {
        if (directories.Count == 0)
        {
            return [];
        }

        var limits = new int[directories.Count];
        var byVolume = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < directories.Count; index++)
        {
            var root = Path.GetPathRoot(directories[index]) ?? directories[index];

            if (!byVolume.TryGetValue(root, out var limit))
            {
                limit = tuning.For(directories[index]).Walk.Threads;
                byVolume[root] = limit;
            }

            limits[index] = limit;
        }

        var ages = new DateTime?[directories.Count];

        ListWorkers.Run(
            directories.Count,
            byVolume.Values.Max(),
            index =>
            {
                // Extended, as a walk's listings are, so both count against the same volume.
                using var slot = budget.Take(LongPath.Extended(directories[index]), limits[index]);
                ages[index] = Of(directories[index], ct);
            },
            ct);

        return ages;
    }

    /// <summary>
    /// The same rule, for a caller whose listings have read both halves already: the directory's own
    /// last write from the listing of its parent, and its entries' from its own listing.
    /// </summary>
    /// <param name="own">The directory's own last write.</param>
    /// <param name="entries">The last write of each of its immediate entries, from one whole listing.</param>
    public static DateTime Of(DateTime own, IEnumerable<DateTime> entries)
    {
        var newest = own;

        foreach (var written in entries)
        {
            if (written > newest)
            {
                newest = written;
            }
        }

        return newest;
    }
}
