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
        new(Task.Run(() => MeasureNow([new ScanRequest(path, bounds)], keep, progress, ct)[0], ct));

    /// <summary>
    /// Every directory among <paramref name="asked"/> in one walk, as <see cref="RootSetWalk"/> walks
    /// them, and every file in a read of its own, as <see cref="MeasureAsync"/> reads one.
    /// </summary>
    public ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep = default,
        CancellationToken ct = default) =>
        MeasureManyAsync(asked, keep, progress: null, ct);

    /// <summary>
    /// The same, reporting the running total of the first path to <paramref name="progress"/>. For
    /// <see cref="DirectoryScanner"/>, which measures one path through here as it measures many.
    /// </summary>
    internal ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep,
        IProgress<ScanSize>? progress,
        CancellationToken ct) =>
        new(Task.Run(() => MeasureNow(asked, keep, progress, ct), ct));

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

    /// <summary>
    /// Each path asked about, in the order asked: a file read directly, and every directory walked with
    /// the others on drives of its kind.
    ///
    /// <para>The directory is asked about first, as <see cref="HardLinkAwareScanner"/> asks, so a
    /// directory costs one attribute read before its walk. A file is asked about only where no
    /// directory is there, and a refusal is met once: the read fails the same way whichever kind is
    /// asked for, so a refused file is answered as a refused directory is.</para>
    /// </summary>
    /// <param name="progress">
    /// The running total of the first path, for <see cref="MeasureAsync"/>, which asks about one.
    /// </param>
    private IReadOnlyList<ScanResult> MeasureNow(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        var results = new ScanResult[asked.Count];
        var directories = new List<int>(asked.Count);

        for (var i = 0; i < asked.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var path = asked[i].Path;

            switch (LongPath.ProbeDirectory(path))
            {
                case PathPresence.Present:
                    directories.Add(i);
                    break;

                case PathPresence.Refused:
                    results[i] = ScanResult.Slow(ScanSize.Zero, _reason) with { Root = RootReach.NotDescribed };
                    break;

                // An absent path measures zero, and that is a complete answer.
                default:
                    results[i] = TryMeasureFile(path, keep) is { } file
                        ? ScanResult.Direct(file.Size, file.WithheldRecent) with { MailStores = file.MailStores }
                        : ScanResult.Slow(ScanSize.Zero, _reason);
                    break;
            }
        }

        // One walk for each set of values rather than one a path: the values differ by the kind of
        // drive, and every path on drives of one kind can share the walk.
        foreach (var walk in directories.GroupBy(i => _tuner.For(asked[i].Path).Walk))
        {
            var each = walk.ToList();
            var walked = Measure([.. each.Select(i => asked[i])], keep, walk.Key, progress, ct);

            for (var j = 0; j < each.Count; j++)
            {
                results[each[j]] = Slow(walked[j]);
            }
        }

        return results;
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

    /// <summary>Walk every directory in <paramref name="asked"/> at once, each to a figure of its own.</summary>
    private static IReadOnlyList<Walked> Measure(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep,
        WalkTuning tuning,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        var tallies = asked.Select(question => new RootTally(question.Bounds.KeepRoot)).ToArray();

        RootSetWalk.Visit<VisitedFolder>(
            [.. asked.Select(question => WalkRoot.Of(question.Path, question.Bounds))],
            tuning,
            (root, holder, contents, left) =>
            {
                var tally = holder?.Tally ?? tallies[root];
                var folder = new VisitedFolder(holder, tally);
                tally.Folders.Add(folder);

                // A folder emptied in place is never among what its removal takes.
                if (holder is null && tally.KeepRoot)
                {
                    folder.Stays();
                }

                // A folder the walk was refused may hold anything, so the removal cannot empty it.
                if (contents.WasRefused)
                {
                    folder.Stays();

                    // Set by the root's own listing alone, which no other thread writes. See RootReach
                    // for why a folder refused below the root does not set it.
                    if (holder is null)
                    {
                        tally.RootRefused = true;
                    }
                }

                foreach (var entry in contents.Entries)
                {
                    // Neither counted nor entered, and its folder stays, as the removal leaves it.
                    if (left?.Contains(entry.Name) == true)
                    {
                        folder.Stays();
                        continue;
                    }

                    // The walk enters it, for every root that does not leave it out.
                    if (entry.IsDirectory)
                    {
                        continue;
                    }

                    // §9, and before the guard for the reason the removal asks it first: the store is
                    // left whatever its age, so it is out of the total and its folders stay.
                    if (MailStore.Is(entry.Name))
                    {
                        tally.Stores.Add(LongPath.Display(entry.FullName));
                        folder.Stays();
                    }

                    // The walk hands over the timestamps the listing read, so asking a file's age here
                    // costs no further I/O (G4). A file the guard keeps contributes nothing, because
                    // the removal will not take it.
                    else if (keep.Protects(entry.NewestFileTime))
                    {
                        Interlocked.Exchange(ref tally.Withheld, 1);
                        folder.Stays();
                    }
                    else
                    {
                        Interlocked.Add(ref tally.Bytes, entry.Length);
                        Interlocked.Increment(ref tally.Entries);
                    }
                }

                // A link is removed as a link: one entry, and nothing on its far side. The guard
                // applies to it exactly as the removal applies it, by the link's own timestamp.
                foreach (var link in contents.Links)
                {
                    if (left?.Contains(link.Name) == true)
                    {
                        folder.Stays();
                    }
                    else if (keep.Protects(link.NewestFileTime))
                    {
                        Interlocked.Exchange(ref tally.Withheld, 1);
                        folder.Stays();
                    }
                    else
                    {
                        Interlocked.Increment(ref tally.Entries);
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
                        folder.Stays();
                    }
                    else if (MailStore.Is(marked.Name))
                    {
                        tally.Stores.Add(LongPath.Display(marked.FullName));
                        folder.Stays();
                    }
                }

                return folder;
            },
            // §5.5: stream partial results, at the walk's interval rather than per file.
            () => progress?.Report(ScanSize.FromLengths(Interlocked.Read(ref tallies[0].Bytes))),
            TimeProvider.System,
            ct);

        return [.. tallies.Select(tally => tally.Walked())];
    }

    /// <summary>One root's figure, added to from every thread the walk runs on.</summary>
    private sealed class RootTally(bool keepRoot)
    {
        public readonly bool KeepRoot = keepRoot;

        public readonly ConcurrentBag<VisitedFolder> Folders = [];

        public readonly ConcurrentBag<string> Stores = [];

        public long Bytes;

        public long Entries;

        // An int rather than a bool because the walk sets it from several threads at once, and
        // Interlocked has no bool overload. Only ever moved to 1, so no ordering question arises.
        public int Withheld;

        public bool RootRefused;

        /// <summary>The figure, read once the walk has ended.</summary>
        public Walked Walked()
        {
            if (RootRefused)
            {
                return new Walked(ScanSize.Zero, false, [], RootReach.NotListed);
            }

            var bytes = Interlocked.Read(ref Bytes);
            var removed = Interlocked.Read(ref Entries) + Folders.Count(folder => !folder.HoldsSomethingThatStays);

            return new Walked(
                new ScanSize(bytes, bytes, Entries: removed),
                Volatile.Read(ref Withheld) == 1,
                [.. Stores.Order(StringComparer.OrdinalIgnoreCase)],
                RootReach.Reached);
        }
    }

    /// <summary>
    /// One folder the walk reached, and whether the removal would leave it standing.
    ///
    /// <para>A folder stays while anything inside it stays, however deep, so a kept file has to mark
    /// every folder above it. The walk is parallel, so no folder's children are all known when it is
    /// reached; each folder instead holds the one above it, and a kept entry walks the
    /// chain upward.</para>
    /// </summary>
    private sealed class VisitedFolder(VisitedFolder? holder, RootTally tally)
    {
        private readonly VisitedFolder? _holder = holder;

        /// <summary>The figure of the root this folder was reached in.</summary>
        public RootTally Tally { get; } = tally;

        private int _stays;

        public bool HoldsSomethingThatStays => Volatile.Read(ref _stays) == 1;

        /// <summary>
        /// Mark this folder and every folder above it. It stops at a folder already marked, because
        /// whatever marked that one goes on to mark everything above it.
        /// </summary>
        public void Stays()
        {
            for (var folder = this; folder is not null && Interlocked.Exchange(ref folder._stays, 1) == 0; folder = folder._holder)
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
    /// </summary>
    private static (ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores)? TryMeasureFile(
        string path,
        MinimumAge keep)
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
                return (ScanSize.Zero, false, [LongPath.Display(file.FullName)]);
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
            return keep.Protects(file)
                ? (ScanSize.Zero, true, [])
                : (new ScanSize(file.Length, file.Length, Entries: 1), false, []);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
