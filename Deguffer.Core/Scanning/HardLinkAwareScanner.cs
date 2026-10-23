using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Measures the bytes a tree holds that nothing outside it also holds: the sum over files whose
/// hard-link count is exactly one. That is the number an eviction of the tree's contents can
/// actually give back, and for a store that hard-links into every consumer — pnpm's, conda's — it
/// is the only defensible figure. Summing file lengths there counts each linked copy, and the
/// reported reclaim would be several times what the disk returns, which is §5.4's lesson on a
/// different subject.
///
/// <para>This walk, and not the MFT, is deliberately the only route. The file record does carry a
/// hard-link count (at 0x12, which <see cref="Mft.MftRecordHeader.Read"/> leaves unread), but the
/// table is only readable elevated, and a link-aware figure that existed only under elevation would
/// disagree with the unelevated one for the same tree. <c>GetFileInformationByHandleEx</c> answers
/// per file at either privilege level — observed on a real hard link, unelevated, before this was
/// built — so one route serves both and the number never depends on how the app was launched.
/// <see cref="ScanResult.Fallback"/> is therefore <see cref="FallbackReason.None"/>: elevating
/// would not change anything, and the offer would be false.</para>
///
/// <para>Each file is read by name, with no handle, where its file system answers that
/// (<see cref="FileInformation.Standard"/>): over a synthesised store of 100,000 files on NTFS, with
/// no real-time antivirus scanning, the read by name took about four fifths of the time a handle per
/// file took (<c>Deguffer.Benchmark links</c>). Both read whatever the listed name is when it is
/// read, a link as the link, and the walk lists no file numbers to compare it with, so a file
/// replaced since the listing is measured as the file of this tree it now is. The figure is a
/// prediction marked approximate either way.</para>
///
/// <para>A file with several links all inside the tree is not counted, though deleting the whole
/// tree would free it. Telling that case apart means enumerating every link's name per multi-linked
/// file, for stores whose layout never produces it — content-addressed files link outward, not to
/// each other — so the cheap reading under-reports in the rare case rather than over-reporting in
/// any, which is the direction §5.4 allows.</para>
/// </summary>
public sealed class HardLinkAwareScanner : IDirectoryScanner
{
    /// <summary>The walk at the shipped values, for a scanner built outside the app (G5).</summary>
    public static readonly HardLinkAwareScanner Default = new(ScanTuner.Shipped);

    private readonly ScanTuner _tuner;
    private readonly FileInformation _files;

    /// <param name="tuner">What each walk runs with, asked as the walk starts.</param>
    public HardLinkAwareScanner(ScanTuner tuner)
        : this(tuner, FileInformation.Default)
    {
    }

    /// <param name="tuner">What each walk runs with, asked as the walk starts.</param>
    /// <param name="files">
    /// How each file's link count and sizes are read, so a test or the benchmark can choose the
    /// route a file system would leave it.
    /// </param>
    internal HardLinkAwareScanner(ScanTuner tuner, FileInformation files)
    {
        _tuner = tuner;
        _files = files;
    }

    public ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        ScanBounds bounds = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        new(WorkerThreads.Shared.RunAsync(() => MeasureNow([new ScanRequest(path, bounds)], [keep], progress, ct)[0][0], ct));

    /// <summary>
    /// Every directory among <paramref name="asked"/> in one walk, as <see cref="RootSetWalk"/> walks
    /// them, and every file in a read of its own.
    /// </summary>
    public ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep = default,
        CancellationToken ct = default) =>
        new(WorkerThreads.Shared.RunAsync(() => (IReadOnlyList<ScanResult>)[.. MeasureNow(asked, [keep], progress: null, ct).Select(readings => readings[0])], ct));

    /// <summary>One walk, tallied twice. See <see cref="IDirectoryScanner.MeasureAgedAsync"/>.</summary>
    public ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, CancellationToken ct = default) =>
        new(WorkerThreads.Shared.RunAsync(() => AgedScan.Of(MeasureNow([new ScanRequest(path)], AgedScan.Keeps(age), progress: null, ct)[0]), ct));

    /// <summary>
    /// The directory is asked about first, and the file only where no directory is there, so each
    /// path costs one attribute read in the ordinary case and a refusal is met once. The read fails
    /// the same way whichever kind is asked for, so a refused file is answered here as a refused
    /// directory would be.
    ///
    /// <para>One reading for each of <paramref name="keeps"/>, in their order, from one reading of each
    /// path.</para>
    /// </summary>
    /// <param name="progress">
    /// The running total of the first path's first reading, for <see cref="MeasureAsync"/>, which asks
    /// about one.
    /// </param>
    private ScanResult[][] MeasureNow(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        var results = new ScanResult[asked.Count][];
        var directories = new List<int>(asked.Count);

        for (var i = 0; i < asked.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var path = asked[i].Path;
            var presence = LongPath.ProbeEntry(path, out var isDirectory);

            if (isDirectory is true)
            {
                directories.Add(i);
                continue;
            }

            if (presence is PathPresence.Present)
            {
                results[i] = [.. MeasureFile(path, keeps, _files)
                    .Select(file => ScanResult.Direct(file.Size, file.WithheldRecent) with { MailStores = file.MailStores })];
                continue;
            }

            var nothing = ScanResult.ByChoice(Approximate(0, 0)) with
            {
                Root = presence is PathPresence.Refused ? RootReach.NotDescribed : RootReach.Reached,
            };

            results[i] = [.. keeps.Select(_ => nothing)];
        }

        // One walk for each set of values, as ParallelEnumerationScanner walks.
        foreach (var walk in directories.GroupBy(i => _tuner.For(asked[i].Path).Walk))
        {
            var each = walk.ToList();
            var walked = Measure([.. each.Select(i => asked[i])], keeps, walk.Key, _files, progress, ct);

            for (var j = 0; j < each.Count; j++)
            {
                results[each[j]] = walked[j];
            }
        }

        return results;
    }

    /// <summary>Always null — this scanner holds no index. See <see cref="ParallelEnumerationScanner"/>.</summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
        DirectorySearch search,
        string root,
        CancellationToken ct = default) => new((IReadOnlyList<string>?)null);

    /// <summary>Nothing is remembered here, so every reading is already taken from the disk.</summary>
    public ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default) =>
        MeasureManyAsync(asked, MinimumAge.Off, ct);

    /// <summary>Nothing is retained between calls, so there is nothing to drop.</summary>
    public void Invalidate()
    {
    }

    /// <summary>Nothing is retained between calls, so there is nothing to keep.</summary>
    public PassHold Hold() => PassHold.Nothing;

    /// <summary>
    /// The walk of every directory in <paramref name="asked"/> Windows describes, at once, each to a
    /// figure of its own for each of <paramref name="keeps"/>. See <see cref="MeasureNow"/>.
    ///
    /// <para>An entry a root's bounds leave out is passed over, neither read nor entered, as the other
    /// walk passes it. A kept root needs nothing here, because this figure counts no entries.</para>
    ///
    /// <para>Each file is read at most once for one root, whichever of its tallies count it. A file
    /// below a path nested inside another is read once for each of them, which costs a second read of
    /// each such file and changes no figure. No provider measuring with this scanner asks about a path
    /// inside another.</para>
    /// </summary>
    private static ScanResult[][] Measure(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        WalkTuning tuning,
        FileInformation files,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        var roots = asked.Select(_ => new RootTally(keeps)).ToArray();
        var route = new ByNameRoute();

        // Every name, because each file counted is opened by its path to ask what else links it.
        RootSetWalk.Visit<RootTally>(
            [.. asked.Select(question => WalkRoot.Of(question.Path, question.Bounds))],
            tuning,
            FileNames.All,
            (root, holder, contents, left) =>
            {
                var measured = holder ?? roots[root];

                // Added up here and handed to the shared tallies once, so the walk's threads meet on
                // them once a directory rather than once a file.
                Span<Sole> sole = stackalloc Sole[measured.Tallies.Length];
                var withheld = 0;

                // See RootReach for why a folder refused below the root does not count.
                if (holder is null && contents.WasRefused)
                {
                    measured.RootRefused = true;
                }

                foreach (var file in contents.Entries)
                {
                    if (file.IsDirectory || left?.Contains(file.Name) == true)
                    {
                        continue;
                    }

                    // §9, named rather than only left out: a store inside a tool's own folder is what
                    // withholds that tool's command, and the plan can only say which store by its path.
                    if (file.IsMailStore)
                    {
                        measured.Stores.Add(LongPath.Display(file.FullName));
                        continue;
                    }

                    // Each guard is asked before the file is read, because the guard is the cheaper
                    // question and the answer is the same either way: a file it keeps is one this store's
                    // eviction will not take, so its sole-linked bytes are not reclaimable here. The file is
                    // read once, for whichever tallies count it, and not at all where every guard keeps it.
                    var read = false;
                    FileInformation.FileStandardInfo? info = null;

                    for (var i = 0; i < measured.Tallies.Length; i++)
                    {
                        if (measured.Tallies[i].Keep.Protects(file.NewestFileTime))
                        {
                            withheld |= 1 << i;
                            continue;
                        }

                        if (!read)
                        {
                            info = files.Standard(file.FullName, route);
                            read = true;
                        }

                        if (info is { NumberOfLinks: 1 } linkedOnce)
                        {
                            sole[i].Allocated += linkedOnce.AllocationSize;
                            sole[i].Logical += linkedOnce.EndOfFile;
                        }
                    }
                }

                for (var i = 0; i < sole.Length; i++)
                {
                    measured.Tallies[i].Add(sole[i], (withheld & (1 << i)) != 0);
                }

                // A file carrying a reparse point holds none of this tree's sole-linked bytes, and one named
                // like a store is still named: the removal does not read which kind of mark a file carries.
                foreach (var marked in contents.ReparseFiles)
                {
                    if (left?.Contains(marked.Name) != true && marked.IsMailStore)
                    {
                        measured.Stores.Add(LongPath.Display(marked.FullName));
                    }
                }

                return measured;
            },
            // §5.5: stream partial results, at the walk's interval rather than per file.
            () => progress?.Report(roots[0].Tallies[0].Size),
            TimeProvider.System,
            ct);

        return [.. roots.Select(root => root.Results(keeps))];
    }

    /// <summary>One root's readings, one <see cref="Tally"/> for each guard.</summary>
    private sealed class RootTally(MinimumAge[] keeps)
    {
        public readonly Tally[] Tallies = [.. keeps.Select(keep => new Tally(keep))];

        public readonly ConcurrentBag<string> Stores = [];

        public bool RootRefused;

        /// <summary>The readings, read once the walk has ended.</summary>
        public ScanResult[] Results(MinimumAge[] keeps)
        {
            if (RootRefused)
            {
                var unlisted = ScanResult.ByChoice(Approximate(0, 0)) with { Root = RootReach.NotListed };
                return [.. keeps.Select(_ => unlisted)];
            }

            IReadOnlyList<string> found = [.. Stores.Order(StringComparer.OrdinalIgnoreCase)];

            return [.. Tallies.Select(tally => ScanResult.ByChoice(tally.Size, tally.WithheldRecent) with { MailStores = found })];
        }
    }

    /// <summary>The sole-linked bytes one directory's files add to one <see cref="Tally"/>.</summary>
    private struct Sole
    {
        public long Allocated;
        public long Logical;
    }

    /// <summary>
    /// The sole-linked bytes one guard's eviction would take from the files a walk lists. Added to from
    /// several threads at once, so every count moves through <see cref="Interlocked"/> and the flag
    /// through <see cref="Volatile"/>.
    /// </summary>
    private sealed class Tally(MinimumAge keep)
    {
        private long _allocated;
        private long _logical;

        // Only ever moved to true, so a write needs no exchange and no ordering question arises.
        private bool _withheld;

        public MinimumAge Keep { get; } = keep;

        public ScanSize Size => Approximate(Interlocked.Read(ref _allocated), Interlocked.Read(ref _logical));

        public bool WithheldRecent => Volatile.Read(ref _withheld);

        /// <summary>
        /// One directory's files: their sole-linked bytes, and whether the guard kept any of them.
        /// </summary>
        public void Add(Sole sole, bool withheld)
        {
            if (sole.Allocated != 0 || sole.Logical != 0)
            {
                Interlocked.Add(ref _allocated, sole.Allocated);
                Interlocked.Add(ref _logical, sole.Logical);
            }

            // Read before written, as ParallelEnumerationScanner's tally reads it.
            if (withheld && !Volatile.Read(ref _withheld))
            {
                Volatile.Write(ref _withheld, true);
            }
        }
    }

    /// <summary>
    /// Both numbers here are read exactly, and the result is still marked approximate, because the
    /// claim it feeds is a prediction: link counts move whenever a project installs or removes
    /// dependencies, and the tool's eviction chooses by its own reference records rather than by
    /// counting links. A figure the next hour can change must not present itself as precise.
    /// </summary>
    /// <remarks>
    /// Counts no entries. What this predicts is what a tool's own eviction frees, and that tool
    /// decides what it removes, so there is no removal of Deguffer's here whose entries could be
    /// counted.
    /// </remarks>
    private static ScanSize Approximate(long allocated, long logical) =>
        new(allocated, logical, IsApproximate: true);

    /// <summary>
    /// The sole-link size of the file at <paramref name="path"/>, mirroring
    /// <see cref="ParallelEnumerationScanner"/>: a single named file is a legitimate subject, and
    /// answering zero for one would make its step unofferable.
    ///
    /// <para>One reading for each of <paramref name="keeps"/>, with the file read at most once.</para>
    /// </summary>
    private static (ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores)[] MeasureFile(
        string path,
        MinimumAge[] keeps,
        FileInformation files)
    {
        if (MailStore.Is(path))
        {
            return [.. keeps.Select(_ => (Approximate(0, 0), false, (IReadOnlyList<string>)[LongPath.Display(path)]))];
        }

        var sole = new Lazy<ScanSize>(() => files.Standard(LongPath.Extended(path), new ByNameRoute()) is { } info
            ? Approximate(
                info.NumberOfLinks == 1 ? info.AllocationSize : 0,
                info.NumberOfLinks == 1 ? info.EndOfFile : 0)
            : Approximate(0, 0));

        return [.. keeps.Select(keep => keep.ProtectsFile(path)
            ? (Approximate(0, 0), true, (IReadOnlyList<string>)[])
            : (sole.Value, false, []))];
    }
}
