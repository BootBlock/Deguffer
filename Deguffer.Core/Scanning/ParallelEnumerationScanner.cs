using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// §5.5's fallback: measure a tree by walking it, with a bounded worker pool.
///
/// This is the approach the audit measured at over ten minutes on a handful of profile subtrees,
/// so it is explicitly *not* the scanner — it is what runs where the MFT cannot be read, and every
/// result it produces carries the reason (see <see cref="FallbackReason"/>).
///
/// It reports file lengths, and the entries a removal would take, and nothing else.
/// <c>FileInfo.Length</c> cannot see how many clusters a
/// compressed or sparse file occupies, and neither can any cheap call: <c>GetCompressedFileSize</c>
/// returns the length again for anything not compressed, and the call that does answer through a
/// handle per file was measured at 156 times the cost of the length pass over a 426 MB cache. The
/// read by name <see cref="Safety.StatByName"/> makes needs no handle, but took about four fifths of
/// a handle's time on a synthesised store, so it would still cost many times the lengths if that
/// ratio held on the cache, which was not measured. So the
/// lengths are the measurement, they are exact, and <see cref="ScanSize.Reclaimable"/> is the
/// number they feed. Nothing here is a guess, which is why <see cref="ScanSize.FromLengths"/> does
/// not mark its results approximate.
/// </summary>
public sealed class ParallelEnumerationScanner : IDirectoryScanner
{
    /// <summary>The walk at the shipped values, for a scanner built outside the app.</summary>
    public static readonly ParallelEnumerationScanner Default = new(ScanTuner.Shipped);

    /// <summary>
    /// One instance per reason, built once with the scanner and sharing its tuner. G5: this type is
    /// stateless apart from the reason it stamps on results, and <see cref="Because"/> sits on the
    /// fallback path — which §6.3 makes the *ordinary* path — so constructing one per measurement
    /// would allocate per directory scanned for no benefit.
    /// </summary>
    private readonly ParallelEnumerationScanner[] _byReason;

    private readonly ScanTuner _tuner;

    private readonly FallbackReason _reason;

    /// <param name="tuner">What each walk runs with, asked as the walk starts.</param>
    public ParallelEnumerationScanner(ScanTuner tuner)
        : this(tuner, FallbackReason.None, new ParallelEnumerationScanner[Enum.GetValues<FallbackReason>().Length])
    {
        foreach (var reason in Enum.GetValues<FallbackReason>())
        {
            _byReason[(int)reason] = reason is FallbackReason.None ? this : new(tuner, reason, _byReason);
        }
    }

    private ParallelEnumerationScanner(ScanTuner tuner, FallbackReason reason, ParallelEnumerationScanner[] byReason)
    {
        _tuner = tuner;
        _reason = reason;
        _byReason = byReason;
    }

    /// <summary>Same walk, attributed to whichever reason sent the caller here.</summary>
    public ParallelEnumerationScanner Because(FallbackReason reason) => _byReason[(int)reason];

    /// <summary>
    /// A file is sized in one read and reported as <see cref="ScanStrategy.DirectRead"/>, dropping
    /// the reason this instance was built with.
    ///
    /// Dropping it is the honest answer rather than a shortcut: a fallback reason explains why a
    /// walk was necessary, and no walk happened. Carrying it would put "scanned by walking
    /// directories, which is slower" on every plan naming a single file, and — where the reason was
    /// <see cref="FallbackReason.NotElevated"/> — offer administrator rights that would change the
    /// measurement not at all.
    ///
    /// This is also the one place a file is measured at all. <see cref="DirectoryScanner"/> reaches
    /// it the ordinary way, because its index cannot resolve a file path and so hands every one of
    /// them here.
    /// </summary>
    public ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        ScanBounds bounds = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        new(Task.Run(() => MeasureEach(path, [keep], bounds, progress, ct)[0], ct));

    /// <summary>One walk, tallied twice. See <see cref="IDirectoryScanner.MeasureAgedAsync"/>.</summary>
    public ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, CancellationToken ct = default) =>
        new(Task.Run(() => AgedScan.Of(MeasureEach(path, AgedScan.Keeps(age), bounds: default, progress: null, ct)), ct));

    /// <summary>
    /// <paramref name="path"/> measured once for each of <paramref name="keeps"/>, in their order, from
    /// one reading of it. <paramref name="progress"/> follows the first.
    ///
    /// <para>So that a caller choosing between routes, <see cref="DirectoryScanner"/>, asks the walk
    /// the same question it asks the table.</para>
    /// </summary>
    internal Task<ScanResult[]> MeasureEachAsync(
        string path,
        MinimumAge[] keeps,
        ScanBounds bounds,
        IProgress<ScanSize>? progress,
        CancellationToken ct) =>
        Task.Run(() => MeasureEach(path, keeps, bounds, progress, ct), ct);

    private ScanResult[] MeasureEach(
        string path,
        MinimumAge[] keeps,
        ScanBounds bounds,
        IProgress<ScanSize>? progress,
        CancellationToken ct) =>
        TryMeasureFile(path, keeps) is { } file
            ? [.. file.Select(reading => ScanResult.Direct(reading.Size, reading.WithheldRecent) with { MailStores = reading.MailStores })]
            : [.. Measure(path, keeps, bounds, _tuner, progress, ct).Select(Slow)];

    /// <summary>
    /// Nothing is remembered here, so every reading is already taken from the disk.
    ///
    /// The guard is deliberately absent. This member exists for the executor's before-and-after
    /// around a tool's own eviction command, and §5.1 leaves that command the authority on what it
    /// removes — a total that excluded recent files would then be subtracted from one the command
    /// did not respect.
    /// </summary>
    public ValueTask<ScanResult> MeasureFromDiskAsync(string path, ScanBounds bounds = default, CancellationToken ct = default) =>
        MeasureAsync(path, MinimumAge.Off, bounds, progress: null, ct);

    /// <summary>
    /// Always null: this scanner holds no index, so it has nothing to search. Answering by walking
    /// here would hide the walk behind the accelerator's signature, and §5.5 requires the slow
    /// route to be visible to the caller that takes it.
    /// </summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
        DirectorySearch search,
        string root,
        CancellationToken ct = default) => new((IReadOnlyList<string>?)null);

    /// <summary>Nothing is retained between calls, so there is nothing to drop.</summary>
    public void Invalidate()
    {
    }

    private ScanResult Slow(Walked measured) =>
        ScanResult.Slow(measured.Size, _reason, measured.WithheldRecent) with
        {
            MailStores = measured.MailStores,
            Root = measured.Root,
        };

    /// <summary>What the walk found, and whether it reached the root to find it.</summary>
    private readonly record struct Walked(
        ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores, RootReach Root);

    /// <summary>
    /// The walk, tallied once for each of <paramref name="keeps"/>. A guard changes nothing about
    /// what is listed or entered, only what each tally counts and which folders it keeps standing,
    /// so one listing serves every tally.
    /// </summary>
    private static Walked[] Measure(
        string path,
        MinimumAge[] keeps,
        ScanBounds bounds,
        ScanTuner tuning,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        switch (LongPath.ProbeDirectory(path))
        {
            case PathPresence.Absent:
                return Each(keeps, new Walked(ScanSize.Zero, false, [], RootReach.Reached));

            // A refused file arrives here too, because TryMeasureFile reads a refusal as "not a
            // file", and the attribute read fails the same way whichever kind is asked for.
            case PathPresence.Refused:
                return Each(keeps, new Walked(ScanSize.Zero, false, [], RootReach.NotDescribed));
        }

        var tallies = keeps.Select((keep, i) => new Tally(keep, 1 << i)).ToArray();

        // Every tally at once: what a removal leaves standing whatever the guard, which marks a
        // folder for all of them.
        var everyTally = (1 << keeps.Length) - 1;

        // Set by the root's own listing alone, which no other thread is reading at the time. See
        // RootReach for why a folder refused below the root does not set it.
        var rootRefused = false;

        var folders = new ConcurrentBag<VisitedFolder>();
        var stores = new ConcurrentBag<string>();
        var leftOut = LeftOutEntries.Of(path, bounds);

        BoundedFileWalk.Visit<VisitedFolder?>(
            LongPath.Extended(path),
            rootState: null,
            tuning,
            (holder, contents, descend) =>
            {
                var folder = new VisitedFolder(holder);
                folders.Add(folder);

                // A folder emptied in place is never among what its removal takes.
                if (holder is null && bounds.KeepRoot)
                {
                    folder.Stays(everyTally);
                }

                // A folder the walk was refused may hold anything, so the removal cannot empty it.
                if (contents.WasRefused)
                {
                    folder.Stays(everyTally);

                    if (holder is null)
                    {
                        rootRefused = true;
                    }
                }

                // Asked once a listing, and of each entry only in a listing that holds one.
                var left = leftOut?.NamesIn(contents);

                foreach (var entry in contents.Entries)
                {
                    // Neither counted nor entered, and its folder stays, as the removal leaves it.
                    if (left?.Contains(entry.Name) == true)
                    {
                        folder.Stays(everyTally);
                    }
                    else if (entry.IsDirectory)
                    {
                        descend(entry, folder);
                    }

                    // §9, and before the guard for the reason the removal asks it first: the store is
                    // left whatever its age, so it is out of the total and its folders stay.
                    else if (MailStore.Is(entry.Name))
                    {
                        stores.Add(LongPath.Display(entry.FullName));
                        folder.Stays(everyTally);
                    }
                    else
                    {
                        foreach (var tally in tallies)
                        {
                            tally.Count(entry.NewestFileTime, entry.Length, folder);
                        }
                    }
                }

                // A link is removed as a link: one entry, and nothing on its far side. The guard
                // applies to it exactly as the removal applies it, by the link's own timestamp.
                foreach (var link in contents.Links)
                {
                    if (left?.Contains(link.Name) == true)
                    {
                        folder.Stays(everyTally);
                    }
                    else
                    {
                        foreach (var tally in tallies)
                        {
                            tally.Count(link.NewestFileTime, length: 0, folder);
                        }
                    }
                }

                // A file carrying a reparse point is counted by nothing here, as the walk never has
                // counted one. One named like a store is still named and keeps its folders, because the
                // removal leaves it whatever its mark means: a OneDrive placeholder and a deduplicated
                // file carry the mark too.
                foreach (var marked in contents.ReparseFiles)
                {
                    if (left?.Contains(marked.Name) == true)
                    {
                        folder.Stays(everyTally);
                    }
                    else if (MailStore.Is(marked.Name))
                    {
                        stores.Add(LongPath.Display(marked.FullName));
                        folder.Stays(everyTally);
                    }
                }
            },
            // §5.5: stream partial results, at the walk's interval rather than per file.
            () => progress?.Report(ScanSize.FromLengths(tallies[0].Total)),
            TimeProvider.System,
            ct);

        if (rootRefused)
        {
            return Each(keeps, new Walked(ScanSize.Zero, false, [], RootReach.NotListed));
        }

        IReadOnlyList<string> found = [.. stores.Order(StringComparer.OrdinalIgnoreCase)];

        return [.. tallies.Select(tally => new Walked(
            new ScanSize(tally.Total, tally.Total, Entries: tally.Entries + folders.Count(folder => !folder.HoldsSomethingThatStays(tally.Bit))),
            tally.WithheldRecent,
            found,
            RootReach.Reached))];
    }

    /// <summary>The same reading for every one of <paramref name="keeps"/>.</summary>
    private static Walked[] Each(MinimumAge[] keeps, Walked walked) => [.. keeps.Select(_ => walked)];

    /// <summary>
    /// What one guard's removal would take from the files and links a walk lists, with
    /// <see cref="Bit"/> naming it in each <see cref="VisitedFolder"/>'s record of what stays.
    ///
    /// <para>Counted from several threads at once, so every field moves through
    /// <see cref="Interlocked"/>.</para>
    /// </summary>
    private sealed class Tally(MinimumAge keep, int bit)
    {
        private long _total;
        private long _entries;

        // An int rather than a bool because Interlocked has no bool overload. Only ever moved to 1,
        // so no ordering question arises.
        private int _withheld;

        public int Bit { get; } = bit;

        public long Total => Interlocked.Read(ref _total);

        public long Entries => Interlocked.Read(ref _entries);

        public bool WithheldRecent => Volatile.Read(ref _withheld) == 1;

        /// <summary>
        /// Count one file or link in <paramref name="folder"/>, unless the guard keeps it, which then
        /// keeps its folders standing too.
        ///
        /// <para>The walk hands over the timestamps the listing read, so asking an entry's age here
        /// costs no further I/O (G4). An entry the guard keeps contributes nothing, because the removal
        /// will not take it.</para>
        /// </summary>
        public void Count(long newestFileTime, long length, VisitedFolder folder)
        {
            if (keep.Protects(newestFileTime))
            {
                Interlocked.Exchange(ref _withheld, 1);
                folder.Stays(Bit);
                return;
            }

            Interlocked.Add(ref _total, length);
            Interlocked.Increment(ref _entries);
        }
    }

    /// <summary>
    /// One folder the walk reached, and which tallies' removals would leave it standing, one bit
    /// for each <see cref="Tally"/>.
    ///
    /// <para>A folder stays while anything inside it stays, however deep, so a kept file has to mark
    /// every folder above it. The walk is parallel, so no folder's children are all known when it is
    /// reached; each folder instead holds the one above it, and a kept entry walks the
    /// chain upward.</para>
    /// </summary>
    private sealed class VisitedFolder(VisitedFolder? holder)
    {
        private readonly VisitedFolder? _holder = holder;

        private int _stays;

        public bool HoldsSomethingThatStays(int tally) => (Volatile.Read(ref _stays) & tally) != 0;

        /// <summary>
        /// Mark this folder and every folder above it for each tally in <paramref name="tallies"/>.
        /// It stops at a folder already marked for all of them, because whatever marked that one goes
        /// on to mark everything above it.
        /// </summary>
        public void Stays(int tallies)
        {
            for (var folder = this; folder is not null && (Interlocked.Or(ref folder._stays, tallies) & tallies) != tallies; folder = folder._holder)
            {
            }
        }
    }

    /// <summary>
    /// The length of <paramref name="path"/> if it is a file, or null for anything else — a
    /// directory, an absent path, or one we were refused. §5.3 makes the refusal ordinary rather
    /// than an error, and a null then leaves the caller to measure the path as a directory, which
    /// answers zero for something that is not there and <see cref="RootReach.NotDescribed"/> for a
    /// refusal.
    ///
    /// Deguffer measured only directories until <c>C:\Windows\MEMORY.DMP</c>, which is a single
    /// file and the largest reclaim it knows about. Answering zero for it — which is what this
    /// scanner did — produces a step nobody can select, because a step with nothing to reclaim is
    /// not offerable.
    ///
    /// <para>One reading for each of <paramref name="keeps"/>, from one read of the file.</para>
    /// </summary>
    private static Walked[]? TryMeasureFile(string path, MinimumAge[] keeps)
    {
        try
        {
            var file = new FileInfo(LongPath.Extended(path));

            if (!file.Exists)
            {
                return null;
            }

            // §9: zero, and named, whatever its age and whatever mark it carries, because the removal
            // leaves it either way. The same answer a kept file gets, for the same reason, with the
            // store's own name on it so the plan can protect it.
            if (MailStore.Is(file.Name))
            {
                return Each(keeps, new Walked(ScanSize.Zero, false, [LongPath.Display(file.FullName)], RootReach.Reached));
            }

            // A link's length is its own, not its target's, and following one would count a tree
            // this scanner never looked inside — the same rule BoundedFileWalk applies to every
            // entry it enumerates.
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            // A file the guard keeps is still a file, so this stays an answer rather than becoming
            // a null that would send the caller off to measure it as a directory. Zero is what the
            // removal will reclaim from it, and a step with nothing to reclaim is not offerable —
            // which is the outcome a protected single file should have.
            return [.. keeps.Select(keep => keep.Protects(file)
                ? new Walked(ScanSize.Zero, true, [], RootReach.Reached)
                : new Walked(new ScanSize(file.Length, file.Length, Entries: 1), false, [], RootReach.Reached))];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
