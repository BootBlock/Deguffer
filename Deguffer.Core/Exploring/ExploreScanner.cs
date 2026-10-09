using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Exploring;

/// <summary>
/// The one way anything learns what a whole volume, or one folder on it, holds.
///
/// <para>Choosing between §5.5's two routes lives here and nowhere else, exactly as it does in
/// <see cref="DirectoryScanner"/> for a single path. Nothing above this knows there is a choice to
/// make, because the choice depends on the volume and the process token rather than on anything
/// about what is being drawn (G1, G2).</para>
///
/// <para>Its own route choice is tested through <see cref="IMftSourceFactory"/>, which the tests
/// substitute directly. <see cref="IExploreScanner"/> is the page's seam instead, for when a scan's
/// results arrive rather than how they were read.</para>
///
/// <para>The snapshot cadence is measured through <paramref name="time"/>, so a test decides when
/// the interval has passed rather than spending real time for it.</para>
/// </summary>
/// <param name="occupancy">What the walk asks about a file whose listing cannot say what it occupies.</param>
public sealed class ExploreScanner(
    IMftSourceFactory? sources = null,
    TimeProvider? time = null,
    ScanTuner? tuning = null,
    IOccupancyProbe? occupancy = null) : IExploreScanner
{
    /// <summary>
    /// How often the walk publishes a tree to draw.
    ///
    /// <para>A snapshot copies every array, so it is not free — and the reason to take one at all is
    /// that a scan of a full drive is long enough that an unchanging window reads as a hung one.
    /// Three quarters of a second is slow enough that the copy is noise against the enumeration and
    /// fast enough to look alive.</para>
    ///
    /// <para>Every disk tool surveyed for this feature — WinDirStat, KDirStat, QDirStat, Filelight,
    /// Baobab, GrandPerspective, Disk Inventory X — refuses to draw its map until the scan finishes,
    /// and DaisyDisk tried live rings and abandoned them. The measured reason is layout instability:
    /// on the Bederson/Shneiderman/Wattenberg change metric a squarified treemap scores 14.82
    /// against slice-and-dice's 0.25, so a map redrawn from growing data rearranges itself
    /// continuously. Deguffer draws its snapshots anyway, and answers that instead — a snapshot is
    /// ordered by <see cref="ExploreChildOrder.ByName"/>, under which a growing child widens where
    /// it already is rather than overtaking its siblings.</para>
    /// </summary>
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(750);

    private readonly IMftSourceFactory _sources = sources ?? VolumeMftSourceFactory.Default;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ScanTuner _tuning = tuning ?? ScanTuner.Shipped;
    private readonly IOccupancyProbe _occupancy = occupancy ?? OccupancyProbe.Default;

    /// <summary>
    /// Scan everything at or below <paramref name="root"/>, which is a volume root or any folder
    /// under one.
    ///
    /// <paramref name="progress"/> receives running counts, and occasionally a snapshot of the tree
    /// so far — §5.5: never block on a complete scan.
    /// </summary>
    public async ValueTask<ExploreScan> ScanAsync(
        string root,
        IProgress<ExploreProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        // Both routes are synchronous and long: reading a table is millions of records, and the
        // walk enumerates before its first yield. On the caller's thread that is a frozen window
        // for the length of the scan, which is the one thing §5.5 asks a scanner not to be.
        return await Task.Run(() => Scan(root, progress, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Scan each of <paramref name="folders"/>, reading each volume's file table once however many
    /// of them are on it, and walking where the table cannot answer.
    ///
    /// <para><b>One read per volume, because each read is the whole table.</b>
    /// <see cref="MftExploreReader.Read"/> cannot reach a folder without reading every record, so
    /// asking it once per folder would read a volume as many times as a search names folders on it.
    /// The tree is rooted at the volume's top and each folder is found in it.</para>
    ///
    /// <para>Where the route is chosen stays here, for the reason <see cref="ScanAsync"/> chooses
    /// it: a caller asking about several folders has no more business knowing there are two routes
    /// than one asking about a single folder.</para>
    /// </summary>
    /// <param name="folders">Full paths of folders, in either form <see cref="Safety.LongPath"/> produces.</param>
    /// <returns>One scan per folder, in the order they were asked for.</returns>
    public async ValueTask<IReadOnlyList<ScannedFolder>> ScanFoldersAsync(
        IReadOnlyList<string> folders,
        IProgress<ExploreProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return await Task.Run(() => ScanFolders(folders, progress, ct), ct).ConfigureAwait(false);
    }

    private ScannedFolder[] ScanFolders(IReadOnlyList<string> folders, IProgress<ExploreProgress>? progress, CancellationToken ct)
    {
        var scans = new ScannedFolder?[folders.Count];
        var reasons = new FallbackReason[folders.Count];
        var onVolumes = new Dictionary<char, List<(int Index, VolumePath Path)>>();

        _tuning.Invalidate();

        for (var i = 0; i < folders.Count; i++)
        {
            if (!VolumePath.TryParse(folders[i], out var volume))
            {
                reasons[i] = FallbackReason.VolumeNotAddressable;
                continue;
            }

            if (!onVolumes.TryGetValue(volume.DriveLetter, out var held))
            {
                onVolumes[volume.DriveLetter] = held = [];
            }

            held.Add((i, volume));
        }

        foreach (var (letter, held) in onVolumes)
        {
            ct.ThrowIfCancellationRequested();

            var (tree, reason, everyRecordRead) = ReadVolume(letter, progress, ct);

            foreach (var (index, volume) in held)
            {
                if (tree is null)
                {
                    reasons[index] = reason;
                    continue;
                }

                var located = MftExploreReader.Locate(tree, volume.Components);

                if (located.Node is { } node)
                {
                    scans[index] = new ScannedFolder(
                        tree, node, ScanStrategy.MasterFileTable, FallbackReason.None,
                        FromIncompleteTable: !everyRecordRead);
                }
                else
                {
                    reasons[index] = located.Reason;
                }
            }
        }

        for (var i = 0; i < folders.Count; i++)
        {
            if (scans[i] is null)
            {
                var walked = Walk(folders[i], reasons[i], progress, ct);
                scans[i] = new ScannedFolder(
                    walked.Tree, walked.Tree.RootNode, walked.Strategy, walked.Fallback, FromIncompleteTable: false);
            }
        }

        return [.. scans.Select(scan => scan!)];
    }

    /// <summary>
    /// The whole of the volume at <paramref name="letter"/> read from its file table into a tree
    /// rooted at its top, or null and why the table could not answer, and whether every record in
    /// use was read and placed (<see cref="MftExploreRead.EveryRecordRead"/>).
    /// </summary>
    private (ExploreTree? Tree, FallbackReason Reason, bool EveryRecordRead) ReadVolume(
        char letter, IProgress<ExploreProgress>? progress, CancellationToken ct)
    {
        if (_tuning.WalkOnly)
        {
            return (null, FallbackReason.WalkChosen, false);
        }

        if (_sources.TryOpen(letter, out var reason) is not { } source)
        {
            return (null, reason, false);
        }

        using (source)
        {
            try
            {
                var top = new VolumePath(letter, [], $"{letter}:{Path.DirectorySeparatorChar}");
                var read = Read(source, top, _tuning.ForVolume(letter).Table, progress, ct);

                return (read.Tree, read.Reason, read.EveryRecordRead);
            }
            catch (IOException)
            {
                // The volume went away mid-read, or the driver refused a read. Neither should end
                // the search, and the walk still answers for every folder on the volume.
                return (null, FallbackReason.MasterFileTableIncomplete, false);
            }
        }
    }

    private ExploreScan Scan(string root, IProgress<ExploreProgress>? progress, CancellationToken ct)
    {
        if (!VolumePath.TryParse(root, out var volume))
        {
            return Walk(root, FallbackReason.VolumeNotAddressable, progress, ct);
        }

        // Each scan is a look at the machine of its own, so a drive attached since the last one is
        // asked what it is rather than taken for the drive that last had its letter.
        _tuning.Invalidate();

        if (_tuning.WalkOnly)
        {
            return Walk(root, FallbackReason.WalkChosen, progress, ct);
        }

        var source = _sources.TryOpen(volume.DriveLetter, out var reason);
        if (source is null)
        {
            return Walk(root, reason, progress, ct);
        }

        using (source)
        {
            try
            {
                var read = Read(source, volume, _tuning.ForVolume(volume.DriveLetter).Table, progress, ct);

                if (read.Tree is { } tree)
                {
                    return ExploreScan.Fast(tree);
                }

                // The table read and could not root a tree where the scan was pointed. Whether that
                // is a route lost or a route that never existed is the reader's judgement, not this
                // one's — a folder reached through a junction has no record whose subtree is its
                // content, and offering administrator rights for that would be an apology for a
                // choice nobody made.
                reason = read.Reason;
            }
            catch (IOException)
            {
                // The volume went away mid-scan, or the driver refused a read. Neither should take
                // the window down, and the walk still answers.
                reason = FallbackReason.MasterFileTableIncomplete;
            }
        }

        // Outside the using deliberately. The walk can run for minutes on a full drive, and holding
        // a raw volume handle open across it serves nothing once the table has been given up on.
        return Walk(root, reason, progress, ct);
    }

    private static MftExploreRead Read(
        IMftSource source,
        VolumePath volume,
        TableTuning tuning,
        IProgress<ExploreProgress>? progress,
        CancellationToken ct)
    {
        var total = source.RecordCount;

        // Both halves come from the same parse. The tree names its root with the path the user
        // reads and locates that root by the components, so the two have to describe one location
        // in one form — see VolumePath.FullPath.
        //
        // No snapshot from this route, and that is not a shortcut. The tree is assembled once,
        // after every record has been read, so there is no partial tree to hand over — and the pass
        // it would interrupt is the whole cost of the route.
        return MftExploreReader.Read(
            source,
            volume.FullPath,
            volume.Components,
            tuning,
            done => progress?.Report(new ExploreProgress(done, total, BytesSeen: 0)),
            ct);
    }

    private ExploreScan Walk(
        string root,
        FallbackReason reason,
        IProgress<ExploreProgress>? progress,
        CancellationToken ct)
    {
        var since = _time.GetTimestamp();

        var tree = WalkExploreReader.Read(
            root,
            _tuning,
            _occupancy,
            (builder, items, bytes) =>
            {
                if (progress is null)
                {
                    return;
                }

                var due = _time.GetElapsedTime(since) >= SnapshotInterval;
                if (due)
                {
                    since = _time.GetTimestamp();
                }

                progress.Report(new ExploreProgress(
                    items,
                    Total: null,
                    bytes,
                    // By name, not by size. The sizes in a partial tree are still growing, and
                    // ordering by one of them is what makes every snapshot a different picture of
                    // the same disk. The finished tree is built by size, once, at the end.
                    due ? builder.Build(ExploreChildOrder.ByName) : null));
            },
            // The scan's own clock, so the walk's reports and the snapshots above keep one time.
            _time,
            ct);

        return ExploreScan.Walked(tree, reason);
    }
}
