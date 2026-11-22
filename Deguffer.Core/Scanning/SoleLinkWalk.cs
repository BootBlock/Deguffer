using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The walk <see cref="HardLinkAwareScanner"/> measures with where no volume index answers: every
/// directory listed, and every file asked what else links it.
///
/// <para>Each file is read by name, with no handle, where its file system answers that
/// (<see cref="FileInformation.Standard"/>): over a synthesised store of 100,000 files on NTFS, with
/// no real-time antivirus scanning, the read by name took about four fifths of the time a handle per
/// file took (<c>Deguffer.Benchmark links</c>). Both read whatever the listed name is when it is
/// read, a link as the link, and the walk lists no file numbers to compare it with, so a file
/// replaced since the listing is measured as the file of this tree it now is. The figure is a
/// prediction marked approximate either way.</para>
///
/// <para>Every reading is <see cref="ScanResult.ByChoice"/>, whatever reason sent a caller here
/// (<see cref="Because"/>). The table answers this figure only where something else is reading it,
/// so a walk here never fell back from a table this figure would have read, and an offer of
/// administrator rights beside it would change the figure not at all.</para>
/// </summary>
/// <param name="tuner">What each walk runs with, asked as the walk starts.</param>
/// <param name="files">How each file's link count and sizes are read.</param>
internal sealed class SoleLinkWalk(ScanTuner tuner, FileInformation files) : IWalkRoute
{
    /// <summary>The same walk: see the remarks on the class.</summary>
    public IWalkRoute Because(FallbackReason reason) => this;

    /// <summary>
    /// Each of <paramref name="asked"/> measured once for each of <paramref name="keeps"/>, in their
    /// orders, from one reading of it: a file read directly, and every directory walked with the others
    /// on drives of its kind, the walks on separate disks at the same time
    /// (<see cref="DirectoryWalks"/>), as <see cref="ParallelEnumerationScanner"/> walks.
    /// </summary>
    /// <param name="running">Where each path's first reading goes as it rises.</param>
    public async Task<ScanResult[][]> MeasureEachAsync(
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
                var walked = Measure([.. each.Select(i => asked[i])], [.. each.Select(i => owns[i])], keeps, walk.Tuning, files, running.Of(each), stop);

                for (var j = 0; j < each.Count; j++)
                {
                    results[each[j]] = walked[j];
                }
            },
            ct).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Each path asked about that is not a directory, answered, with each directory's slot left for its
    /// walk, and the walks the directories need. On a worker thread, because it reads each path and
    /// asks each volume's device.
    ///
    /// <para>The directory is asked about first, and the file only where no directory is there, so each
    /// path costs one attribute read in the ordinary case and a refusal is met once. The read fails
    /// the same way whichever kind is asked for, so a refused file is answered here as a refused
    /// directory would be.</para>
    ///
    /// <para>One reading for each of <paramref name="keeps"/>, in their order, from one reading of each
    /// path.</para>
    /// </summary>
    private (ScanResult[][] Results, long[] Owns, IReadOnlyList<IReadOnlyList<DirectoryWalk>> Walks) Probe(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        RunningPaths running,
        CancellationToken ct)
    {
        var results = new ScanResult[asked.Count][];
        var directories = new List<int>(asked.Count);

        // Each entry's own time, from the read that found it: a directory's dates it with its listing,
        // and a file's is the time the guard judges it by.
        var owns = new long[asked.Count];

        for (var i = 0; i < asked.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var path = asked[i].Path;
            var presence = LongPath.ProbeEntry(path, out var isDirectory, out owns[i]);

            if (isDirectory is true)
            {
                directories.Add(i);
                continue;
            }

            if (presence is PathPresence.Present)
            {
                results[i] = MeasureFile(path, owns[i], keeps, files);

                // A file is read whole, so its reading is final, and it counts before any walk starts.
                running.Report(i, results[i][0].Size);
                continue;
            }

            var nothing = ScanResult.ByChoice(ScanSize.Approximate(0, 0)) with
            {
                Root = presence is PathPresence.Refused ? RootReach.NotDescribed : RootReach.Reached,
            };

            results[i] = [.. keeps.Select(_ => nothing)];
        }

        return (results, owns, DirectoryWalks.Plan(asked, directories, tuner));
    }

    /// <summary>
    /// The walk of every directory in <paramref name="asked"/> Windows describes, at once, each to a
    /// figure of its own for each of <paramref name="keeps"/>. See <see cref="Probe"/>.
    ///
    /// <para>An entry a root's bounds leave out is passed over, neither read nor entered, as the other
    /// walk passes it. A kept root needs nothing here, because this figure counts no entries.</para>
    ///
    /// <para>Each file is read at most once for one root, whichever of its tallies count it. A file
    /// below a path nested inside another is read once for each of them, which costs a second read of
    /// each such file and changes no figure. No provider measuring with this scanner asks about a path
    /// inside another.</para>
    /// </summary>
    /// <param name="owns">
    /// Each root's own time, from the attribute read that found it, in the order of
    /// <paramref name="asked"/>. See <see cref="ScanResult.LastWritten"/>.
    /// </param>
    private static ScanResult[][] Measure(
        IReadOnlyList<ScanRequest> asked,
        IReadOnlyList<long> owns,
        MinimumAge[] keeps,
        WalkTuning tuning,
        FileInformation files,
        RunningPaths running,
        CancellationToken ct)
    {
        var roots = asked.Select((_, i) => new RootTally(owns[i], keeps)).ToArray();
        var route = new ByNameRoute();

        // Every name, because each file counted is asked what else links it by its name, relative to
        // the directory the walk holds open while this reads it.
        RootSetWalk.Visit<RootTally>(
            [.. asked.Select(question => WalkRoot.Of(question.Path, question.Bounds))],
            tuning,
            FileNames.All,
            DirectoryHold.DuringCallback,
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

                // The root's age, from its own listing alone, every entry included. See ScanResult.LastWritten.
                if (holder is null)
                {
                    measured.Newest = OneLevelAge.Of(measured.Own, contents);
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
                            info = files.Standard(contents.Handle, file, route);
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
            () => running.Report(roots.Length, i => roots[i].Tallies[0].Size),
            TimeProvider.System,
            ct);

        return [.. roots.Select(root => root.Results(keeps))];
    }

    /// <summary>One root's readings, one <see cref="Tally"/> for each guard.</summary>
    /// <param name="own">The root's own time, from the read that found it. See <see cref="OneLevelAge.Of"/>.</param>
    private sealed class RootTally(long own, MinimumAge[] keeps)
    {
        public readonly long Own = own;

        public readonly Tally[] Tallies = [.. keeps.Select(keep => new Tally(keep))];

        public readonly ConcurrentBag<string> Stores = [];

        public bool RootRefused;

        /// <summary>The root's age, written by its own listing alone, as <see cref="RootRefused"/> is.</summary>
        public long Newest = OneLevelAge.Unknown;

        /// <summary>The readings, read once the walk has ended.</summary>
        public ScanResult[] Results(MinimumAge[] keeps)
        {
            if (RootRefused)
            {
                var unlisted = ScanResult.ByChoice(ScanSize.Approximate(0, 0)) with { Root = RootReach.NotListed };
                return [.. keeps.Select(_ => unlisted)];
            }

            IReadOnlyList<string> found = [.. Stores.Order(StringComparer.OrdinalIgnoreCase)];

            var age = OneLevelAge.ToUtc(Newest);

            return [.. Tallies.Select(tally => ScanResult.ByChoice(tally.Size, tally.WithheldRecent) with { MailStores = found, LastWritten = age })];
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

        public ScanSize Size => ScanSize.Approximate(Interlocked.Read(ref _allocated), Interlocked.Read(ref _logical));

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
    /// The sole-link size of the file at <paramref name="path"/>, mirroring
    /// <see cref="ParallelEnumerationScanner"/>: a single named file is a legitimate subject, and
    /// answering zero for one would make its step unofferable.
    ///
    /// <para>One reading for each of <paramref name="keeps"/>, with the file opened at most once and
    /// its age judged from the attribute read that found it, <paramref name="newestFileTime"/>, rather
    /// than by reading it again for each guard.</para>
    /// </summary>
    private static ScanResult[] MeasureFile(
        string path,
        long newestFileTime,
        MinimumAge[] keeps,
        FileInformation files)
    {
        if (MailStore.Is(path))
        {
            IReadOnlyList<string> store = [LongPath.Display(path)];

            return [.. keeps.Select(_ => ScanResult.Direct(ScanSize.Approximate(0, 0)) with { MailStores = store })];
        }

        var sole = new Lazy<ScanSize>(() => files.Standard(LongPath.Extended(path), new ByNameRoute()) is { } info
            ? ScanSize.Approximate(
                info.NumberOfLinks == 1 ? info.AllocationSize : 0,
                info.NumberOfLinks == 1 ? info.EndOfFile : 0)
            : ScanSize.Approximate(0, 0));

        return [.. keeps.Select(keep => keep.Protects(newestFileTime)
            ? ScanResult.Direct(ScanSize.Approximate(0, 0), withheldRecent: true) with { FileKept = true }
            : ScanResult.Direct(sole.Value) with { FileKept = false })];
    }
}
