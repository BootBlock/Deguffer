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
public sealed partial class ParallelEnumerationScanner : IDirectoryScanner
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

    public ScanTuner Tuning => _tuner;

    private readonly FallbackReason _reason;

    private readonly ListDirectory _list;

    private readonly TimeProvider _clock;

    private readonly ReadEntry _read;

    /// <param name="tuner">What each walk runs with, asked as the walk starts.</param>
    public ParallelEnumerationScanner(ScanTuner tuner)
        : this(tuner, DirectoryQuery.List, TimeProvider.System)
    {
    }

    /// <summary>
    /// The scanner above, listing each directory through <paramref name="list"/> and timing its reports
    /// by <paramref name="clock"/>: the seam a test holds a walk part-way through, to read what it has
    /// reported before it ends.
    /// </summary>
    internal ParallelEnumerationScanner(ScanTuner tuner, ListDirectory list, TimeProvider clock)
        : this(tuner, list, clock, LongPath.ReadEntry)
    {
    }

    /// <summary>
    /// The scanner above, reading each path it is handed through <paramref name="read"/>: the seam a
    /// test counts the reads of a file through, and proves they overlap by.
    /// </summary>
    internal ParallelEnumerationScanner(ScanTuner tuner, ListDirectory list, TimeProvider clock, ReadEntry read)
        : this(tuner, FallbackReason.None, new ParallelEnumerationScanner[Enum.GetValues<FallbackReason>().Length], list, clock, read)
    {
        foreach (var reason in Enum.GetValues<FallbackReason>())
        {
            _byReason[(int)reason] = reason is FallbackReason.None ? this : new(tuner, reason, _byReason, list, clock, read);
        }
    }

    private ParallelEnumerationScanner(
        ScanTuner tuner,
        FallbackReason reason,
        ParallelEnumerationScanner[] byReason,
        ListDirectory list,
        TimeProvider clock,
        ReadEntry read)
    {
        _tuner = tuner;
        _reason = reason;
        _byReason = byReason;
        _list = list;
        _clock = clock;
        _read = read;
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
    public async ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        ScanBounds bounds = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default)
    {
        ScanRequest[] asked = [new(path, bounds)];

        return (await MeasureEachAsync(asked, [keep], RunningTotal.Over(asked, progress), ct).ConfigureAwait(false))[0][0];
    }

    /// <summary>
    /// Every directory among <paramref name="asked"/> in one walk for each set of values, as
    /// <see cref="RootSetWalk"/> walks them, the walks on separate disks at the same time, and every
    /// file in a read of its own, as <see cref="MeasureAsync"/> reads one.
    /// </summary>
    public async ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        [.. (await MeasureEachAsync(asked, [keep], RunningTotal.Over(asked, progress), ct).ConfigureAwait(false)).Select(readings => readings[0])];

    /// <summary>One walk, tallied twice. See <see cref="IDirectoryScanner.MeasureAgedAsync"/>.</summary>
    public async ValueTask<AgedScan> MeasureAgedAsync(
        string path, MinimumAge age, IProgress<ScanSize>? progress = null, CancellationToken ct = default)
    {
        ScanRequest[] asked = [new(path)];

        return AgedScan.Of((await MeasureEachAsync(asked, AgedScan.Keeps(age), RunningTotal.Over(asked, progress), ct).ConfigureAwait(false))[0]);
    }

    /// <summary>
    /// Each of <paramref name="asked"/> measured once for each of <paramref name="keeps"/>, in their
    /// orders, from one reading of it. Each path's first reading goes to <paramref name="running"/> as
    /// it rises.
    ///
    /// <para>So that a caller choosing between routes, <see cref="DirectoryScanner"/>, asks the walk
    /// the same question it asks the table.</para>
    ///
    /// <para>A file is read directly, and every directory walked with the others on drives of its kind,
    /// the walks on separate disks at the same time (<see cref="DirectoryWalks"/>).</para>
    /// </summary>
    internal async Task<ScanResult[][]> MeasureEachAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        RunningPaths running,
        CancellationToken ct)
    {
        var (results, owns, walks) = await WorkerThreads.Shared.RunAsync(() => Probe(asked, keeps, running, ct), ct).ConfigureAwait(false);

        // Each walk writes the slots of its own paths, which no other walk writes.
        await DirectoryWalks.RunAsync(
            walks,
            (walk, stop) =>
            {
                var each = walk.Indexes;
                var walked = Measure([.. each.Select(i => asked[i])], [.. each.Select(i => owns[i])], keeps, walk.Tuning, running.Of(each), stop);

                for (var j = 0; j < each.Count; j++)
                {
                    results[each[j]] = [.. walked[j].Select(Slow)];
                }
            },
            ct).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Nothing is remembered here, so every reading is already taken from the disk.
    ///
    /// The guard is deliberately absent. This member exists for the executor's before-and-after
    /// around a tool's own eviction command, and §5.1 leaves that command the authority on what it
    /// removes — a total that excluded recent files would then be subtracted from one the command
    /// did not respect.
    /// </summary>
    public ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default) =>
        MeasureManyAsync(asked, MinimumAge.Off, ct: ct);

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

    /// <summary>Nothing is retained between calls, so there is nothing to keep.</summary>
    public PassHold Hold() => PassHold.Nothing;

    /// <summary>
    /// Each path asked about that is not a directory, answered in the order asked, read once for each
    /// of <paramref name="keeps"/>, with each directory's slot left for its walk, and the walks the
    /// directories need. On a worker thread, because it reads each path and asks each volume's device.
    ///
    /// <para><b>One attribute read a path, whichever kind it is.</b> The read describes a directory's
    /// time for its walk, and a file's length and times for its measurement. A refusal is met once,
    /// and a refused file is answered as a refused directory is, because the read fails the same way
    /// whichever kind is there.</para>
    ///
    /// <para><b>Several paths at once</b>, through <see cref="DiskBoundWork"/>, because a row may offer
    /// thousands of single files. Under the volume's walk tuning and the shared listing budget, so the
    /// reads share the disk with the walks of the rows planned beside this one, and answered in the
    /// order asked.</para>
    /// </summary>
    private (ScanResult[][] Results, long[] Owns, IReadOnlyList<IReadOnlyList<DirectoryWalk>> Walks) Probe(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        RunningPaths running,
        CancellationToken ct)
    {
        var read = DiskBoundWork.Each(
            [.. asked.Select(question => question.Path)],
            _tuner,
            ListingBudget.Shared,
            path => (Presence: _read(path, out var reading), Reading: reading),
            ct);

        var results = new ScanResult[asked.Count][];
        var directories = new List<int>(asked.Count);

        // Each directory's own time, from the read that found it, which dates it with its listing.
        var owns = new long[asked.Count];

        for (var i = 0; i < asked.Count; i++)
        {
            var (presence, reading) = read[i];

            switch (presence)
            {
                case PathPresence.Present when reading.IsDirectory:
                    owns[i] = reading.NewestFileTime;
                    directories.Add(i);
                    break;

                case PathPresence.Present:
                    results[i] = MeasureFile(asked[i].Path, reading, keeps);

                    // A file is read whole, so its reading is final, and it counts before any walk starts.
                    running.Report(i, results[i][0].Size);
                    break;

                case PathPresence.Refused:
                    results[i] = [.. Each(keeps, new Walked(ScanSize.Zero, false, [], RootReach.NotDescribed)).Select(Slow)];
                    break;

                // An absent path measures zero, and that is a complete answer.
                default:
                    results[i] = [.. Each(keeps, new Walked(ScanSize.Zero, false, [], RootReach.Reached)).Select(Slow)];
                    running.Report(i, results[i][0].Size);
                    break;
            }
        }

        return (results, owns, DirectoryWalks.Plan(asked, directories, _tuner));
    }

    private ScanResult Slow(Walked measured) =>
        ScanResult.Slow(measured.Size, _reason, measured.WithheldRecent) with
        {
            MailStores = measured.MailStores,
            Root = measured.Root,
            LastWritten = measured.LastWritten,
        };

    /// <summary>
    /// What the walk found, whether it reached the root to find it, and the root's age where it is a
    /// directory listed to the end. See <see cref="ScanResult.LastWritten"/>.
    /// </summary>
    private readonly record struct Walked(
        ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores, RootReach Root, DateTime? LastWritten = null);

    /// <summary>
    /// Walk every directory in <paramref name="asked"/> at once, each to a figure of its own for each
    /// of <paramref name="keeps"/>. A guard changes nothing about what is listed or entered, only what
    /// each tally counts and which folders it keeps standing, so one listing serves every tally.
    ///
    /// <para>Each directory's files are added up in locals and handed to the shared tallies once, so
    /// the walk's threads meet on them once a directory rather than once a file. Progress reads the
    /// shared totals, which then trail by at most the directory each thread is adding up.</para>
    /// </summary>
    /// <param name="owns">
    /// Each root's own time, from the attribute read that found it, as
    /// <see cref="EntryReading.NewestFileTime"/> gives it, in the order of
    /// <paramref name="asked"/>.
    /// </param>
    private Walked[][] Measure(
        IReadOnlyList<ScanRequest> asked,
        IReadOnlyList<long> owns,
        MinimumAge[] keeps,
        WalkTuning tuning,
        RunningPaths running,
        CancellationToken ct)
    {
        var roots = asked.Select((question, i) => new RootTally(question.Bounds.KeepRoot, owns[i], keeps)).ToArray();

        // Every tally at once: what a removal leaves standing whatever the guard, which marks a
        // folder for all of them.
        var everyTally = (1 << keeps.Length) - 1;

        // A file's name is read only to ask whether it is a store, so no other file is named.
        RootSetWalk.Visit<VisitedFolder>(
            [.. asked.Select(question => WalkRoot.Of(question.Path, question.Bounds))],
            tuning,
            FileNames.Stores,
            DirectoryHold.None,
            _list,
            (root, holder, contents, left) =>
            {
                var measured = holder?.Root ?? roots[root];
                var folder = new VisitedFolder(holder, measured);
                Span<Counted> counted = stackalloc Counted[measured.Tallies.Length];

                // The tallies whose removals leave this folder standing, and the tallies whose guard
                // kept something in it, which leaves it standing for them too.
                var stays = 0;
                var withheld = 0;

                // A folder emptied in place is never among what its removal takes.
                if (holder is null && measured.KeepRoot)
                {
                    stays = everyTally;
                }

                // A folder the walk was refused may hold anything, so the removal cannot empty it.
                if (contents.WasRefused)
                {
                    stays = everyTally;

                    // Set by the root's own listing alone, which no other thread writes. See RootReach
                    // for why a folder refused below the root does not set it.
                    if (holder is null)
                    {
                        measured.RootRefused = true;
                    }
                }

                // The root's age, from the listing the measurement needed anyway, by the same lone
                // writer. Every entry dates it, those the figure leaves out included.
                if (holder is null)
                {
                    measured.Newest = OneLevelAge.Of(measured.Own, contents);
                }

                foreach (var entry in contents.Entries)
                {
                    // Neither counted nor entered, and its folder stays, as the removal leaves it.
                    if (left?.Contains(entry.Name) == true)
                    {
                        stays = everyTally;
                        continue;
                    }

                    // The walk enters it, for every root that does not leave it out.
                    if (entry.IsDirectory)
                    {
                        continue;
                    }

                    // §9, and before the guard for the reason the removal asks it first: the store is
                    // left whatever its age, so it is out of the total and its folders stay.
                    if (entry.IsMailStore)
                    {
                        measured.Stores.Add(LongPath.Display(entry.FullName));
                        stays = everyTally;
                    }
                    else
                    {
                        Count(measured.Tallies, counted, entry.NewestFileTime, entry.Length, ref withheld);
                    }
                }

                // A link is removed as a link: one entry, and nothing on its far side. The guard
                // applies to it exactly as the removal applies it, by the link's own timestamp.
                foreach (var link in contents.Links)
                {
                    if (left?.Contains(link.Name) == true)
                    {
                        stays = everyTally;
                    }
                    else
                    {
                        Count(measured.Tallies, counted, link.NewestFileTime, length: 0, ref withheld);
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
                        stays = everyTally;
                    }
                    else if (marked.IsMailStore)
                    {
                        measured.Stores.Add(LongPath.Display(marked.FullName));
                        stays = everyTally;
                    }
                }

                for (var i = 0; i < counted.Length; i++)
                {
                    measured.Tallies[i].Add(counted[i], withheld);
                }

                folder.Stays(stays | withheld);
                return folder;
            },
            // §5.5: stream partial results, at the walk's interval rather than per file.
            () => running.Report(roots.Length, i => ScanSize.FromLengths(roots[i].Tallies[0].Total)),
            _clock,
            ct);

        return [.. roots.Select(root => root.Readings(keeps))];
    }

    /// <summary>The same reading for every one of <paramref name="keeps"/>.</summary>
    private static Walked[] Each(MinimumAge[] keeps, Walked walked) => [.. keeps.Select(_ => walked)];

    /// <summary>
    /// The file at <paramref name="path"/>, measured from the attribute read that found it, once for
    /// each of <paramref name="keeps"/>.
    ///
    /// <para>Deguffer measured only directories until <c>C:\Windows\MEMORY.DMP</c>, which is a single
    /// file and the largest reclaim it knows about. Answering zero for it, which is what this scanner
    /// did, produces a step nobody can select, because a step with nothing to reclaim is not
    /// offerable.</para>
    /// </summary>
    private ScanResult[] MeasureFile(string path, EntryReading reading, MinimumAge[] keeps)
    {
        // §9: zero, and named, whatever its age and whatever mark it carries, because the removal
        // leaves it either way. The same answer a kept file gets, for the same reason, with the store's
        // own name on it so the plan can protect it.
        if (MailStore.Is(path))
        {
            IReadOnlyList<string> store = [LongPath.Display(LongPath.Extended(path))];

            return [.. keeps.Select(_ => ScanResult.Direct(ScanSize.Zero) with { MailStores = store })];
        }

        // A link's length is its own, not its target's, and following one would count a tree this
        // scanner never looked inside: the same rule BoundedFileWalk applies to every entry it
        // enumerates. So it measures zero, as a path with nothing there does, and the guard still
        // judges it by its own times, as the removal judges it.
        if (reading.IsReparsePoint)
        {
            var link = Slow(new Walked(ScanSize.Zero, false, [], RootReach.Reached));

            return [.. keeps.Select(keep => link with { FileKept = keep.Protects(reading.NewestFileTime) })];
        }

        // A file the guard keeps is still a file, so this stays an answer rather than a zero for a
        // path that is not there. Zero is what the removal will reclaim from it, and a step with
        // nothing to reclaim is not offerable, which is the outcome a protected single file should have.
        return [.. keeps.Select(keep => keep.Protects(reading.NewestFileTime)
            ? ScanResult.Direct(ScanSize.Zero, withheldRecent: true) with { FileKept = true }
            : ScanResult.Direct(new ScanSize(reading.Length, reading.Length, Entries: 1)) with { FileKept = false })];
    }
}
