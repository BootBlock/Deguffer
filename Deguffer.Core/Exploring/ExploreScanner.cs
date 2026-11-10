using Deguffer.Core.Configuration;
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
/// <param name="sources">
/// Opens each volume's table. Never defaulted, so no scan reads this machine's tables unless it was
/// handed them: the app hands it <see cref="Safety.IUserEnvironment.VolumeTables"/>.
/// </param>
/// <param name="occupancy">What the walk asks about a file whose listing cannot say what it occupies.</param>
public sealed class ExploreScanner(
    IMftSourceFactory sources,
    TimeProvider? time = null,
    ScanTuner? tuning = null,
    IOccupancyProbe? occupancy = null) : IExploreScanner
{
    private readonly IMftSourceFactory _sources = sources;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ScanTuner _tuning = tuning ?? ScanTuner.Shipped;
    private readonly IOccupancyProbe _occupancy = occupancy ?? OccupancyProbe.Default;

    /// <summary>
    /// How a scan assembles a snapshot, on either route. A test sets one that runs it on the spot, so
    /// which report carries the snapshot does not depend on the thread pool.
    /// </summary>
    internal Func<Action, Task> AssembleSnapshot { get; init; } = ScanSnapshots.InBackground;

    /// <summary>
    /// How a folder is walked, where the table does not answer for it or is raced. A test sets one that records the folder
    /// and reads nothing, so a scan of a drive letter or a share only the fakes stand for never walks
    /// whatever the machine running the suite has there, or looks a share's server up on the network.
    /// </summary>
    internal ExploreWalk Walker { get; init; } = WalkExploreReader.Read;

    /// <summary>
    /// Scan everything at or below <paramref name="root"/>, which is a volume root or any folder
    /// under one.
    ///
    /// <paramref name="progress"/> receives running counts, and occasionally a snapshot of the tree
    /// so far — §5.5: never block on a complete scan.
    ///
    /// <para><b>A folder is raced, a volume is read.</b> Under <see cref="ScanRoute.Auto"/>, where the
    /// table opens, a scan of a folder walks the folder while the table is read and takes whichever
    /// answers first (<see cref="RouteRace"/>). The table's cost is the volume's whatever is asked of
    /// it (<see cref="MftExploreReader.Read"/>), so a small folder is answered by the walk in a
    /// fraction of it, and a large one by the table. A scan of a volume's root is not raced, because
    /// the walk it would race is the whole volume, which §5.5 measured at over ten minutes.
    /// <see cref="ScanRoute.Table"/> reads the table for a folder too, waited for while it is
    /// read.</para>
    /// </summary>
    public async ValueTask<ExploreScan> ScanAsync(
        string root,
        IProgress<ExploreProgress>? progress = null,
        Func<ExploreChildOrder>? snapshotOrder = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var snapshots = new SnapshotAssembly(AssembleSnapshot, snapshotOrder ?? SnapshotAssembly.ByName.Order);
        var opened = await OnWorker(() => Open(root), ct).ConfigureAwait(false);

        if (opened.Source is not { } source)
        {
            return await OnWorker(() => Walk(root, opened.Reason, progress, snapshots, ct), ct).ConfigureAwait(false);
        }

        FallbackReason declined;

        try
        {
            if (opened.Volume.Components.Count > 0 && _tuning.Route is ScanRoute.Auto)
            {
                return await RaceAsync(root, opened.Volume, source, progress, snapshots, ct).ConfigureAwait(false);
            }

            var read = await OnWorker(() => ReadTable(source, opened.Volume, progress, snapshots, ct), ct).ConfigureAwait(false);

            if (read.Scan is { } scan)
            {
                return scan;
            }

            declined = read.Declined;
        }
        finally
        {
            // Every read of it has ended by here, a raced read that lost included, which the race
            // waits for before it returns.
            source.Dispose();
        }

        // After the volume is closed deliberately. The walk can run for minutes on a full drive, and
        // holding a raw volume handle open across it serves nothing once the table has been given up on.
        return await OnWorker(() => Walk(root, declined, progress, snapshots, ct), ct).ConfigureAwait(false);
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
    ///
    /// <para><b>Disks at once, volumes on one disk in turn.</b> The folders on each set of physical
    /// disks are scanned at the same time as those on every other set (<see cref="FolderPipelines"/>),
    /// so folders on two disks cost the longer of the two scans, not their sum.</para>
    ///
    /// <para><paramref name="progress"/> is told counts only, never a snapshot: a caller asking
    /// about several folders draws nothing until it has every one. The counts are those of every
    /// pass added together (<see cref="FolderScanProgress"/>).</para>
    /// </summary>
    /// <param name="folders">Full paths of folders, in either form <see cref="Safety.LongPath"/> produces.</param>
    /// <returns>One scan per folder, in the order they were asked for.</returns>
    public ValueTask<IReadOnlyList<ScannedFolder>> ScanFoldersAsync(
        IReadOnlyList<string> folders,
        IProgress<ExploreProgress>? progress = null,
        CancellationToken ct = default) =>
        ScanFoldersAsync(folders, unlisted: null, progress, ct);

    /// <summary>
    /// <see cref="ScanFoldersAsync(IReadOnlyList{string}, IProgress{ExploreProgress}?, CancellationToken)"/>,
    /// told which folders below each one its caller passes over, so the walk does not list them.
    ///
    /// <para><b>Only the walk is told.</b> Listing a folder is the walk's cost, and on a system drive
    /// the places a duplicate search passes over by default were measured at 41% of a warm walk's
    /// time and a quarter of its entries. The file table is read whole whatever is asked of it, so
    /// the route that reads it leaves nothing out, and the caller passes over the same places in
    /// what it reads.</para>
    /// </summary>
    /// <param name="unlisted">For each folder, what not to list below it, or null where everything is listed.</param>
    internal async ValueTask<IReadOnlyList<ScannedFolder>> ScanFoldersAsync(
        IReadOnlyList<string> folders,
        IReadOnlyList<LeavesUnlisted?>? unlisted,
        IProgress<ExploreProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folders);

        // Each scan is a look at the machine of its own, as in Scan. Planned on a worker thread,
        // because learning which disks a volume is on opens its devices and waits on them.
        var pipelines = await WorkerThreads.Shared.RunAsync(
            () =>
            {
                _tuning.Invalidate();
                return FolderPipelines.Of(folders, _tuning.StorageOf);
            },
            ct).ConfigureAwait(false);

        var scans = new ScannedFolder[folders.Count];
        var sum = progress is null ? null : new FolderScanProgress(progress);

        await PipelinesAtOnce.RunAsync(
            pipelines,
            (pipeline, stop) => WorkerThreads.Shared.RunAsync(
                () =>
                {
                    ScanPipeline(pipeline, folders, unlisted, scans, sum, stop);
                    return true;
                },
                stop),
            ct).ConfigureAwait(false);

        return scans;
    }

    /// <summary>
    /// One pipeline's volumes, one after another: each volume's file table read once for every folder
    /// on it, then the walk of each folder the tables could not answer, volume by volume. Writes each
    /// folder's scan into its own slot of <paramref name="scans"/>, which no other pipeline writes.
    /// </summary>
    private void ScanPipeline(
        IReadOnlyList<VolumeFolders> pipeline,
        IReadOnlyList<string> folders,
        IReadOnlyList<LeavesUnlisted?>? unlisted,
        ScannedFolder[] scans,
        FolderScanProgress? sum,
        CancellationToken ct)
    {
        List<(int Index, FallbackReason Reason)> toWalk = [];

        foreach (var volume in pipeline)
        {
            ct.ThrowIfCancellationRequested();

            if (volume.DriveLetter is not { } letter)
            {
                toWalk.AddRange(volume.Folders.Select(folder => (folder.Index, FallbackReason.VolumeNotAddressable)));
                continue;
            }

            var (tree, reason, everyRecordRead) = ReadVolume(letter, sum?.Pass(), ct);

            foreach (var folder in volume.Folders)
            {
                if (tree is null)
                {
                    toWalk.Add((folder.Index, reason));
                    continue;
                }

                var located = MftExploreRooting.Locate(tree, folder.Volume!.Value.Components);

                if (located.Node is { } node)
                {
                    scans[folder.Index] = new ScannedFolder(
                        tree, node, ScanStrategy.MasterFileTable, FallbackReason.None,
                        FromIncompleteTable: !everyRecordRead);
                }
                else
                {
                    toWalk.Add((folder.Index, located.Reason));
                }
            }
        }

        foreach (var (index, reason) in toWalk)
        {
            // No snapshots: what asks for several folders draws nothing until they are all read, and
            // a snapshot nobody draws is a pass over the tree for nothing.
            var walked = Walk(folders[index], reason, sum?.Pass(), snapshots: null, ct, unlisted?[index]);
            scans[index] = new ScannedFolder(
                walked.Tree, walked.Tree.RootNode, walked.Strategy, walked.Fallback, FromIncompleteTable: false);
        }
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

        if (_sources.TryOpenTable(letter, out var reason) is not { } source)
        {
            return (null, reason, false);
        }

        using (source)
        {
            try
            {
                var top = new VolumePath(letter, [], $"{letter}:{Path.DirectorySeparatorChar}");
                // No snapshots, for the reason the walks below take none.
                var read = Read(source, top, _tuning.ForVolume(letter).Table, progress, snapshots: null, ct);

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

    /// <summary>
    /// Run one step of a scan on a thread of its own. Every step is synchronous and long, or waits on
    /// a device: reading a table is millions of records, the walk enumerates before its first yield,
    /// and opening a volume waits on it. On the caller's thread that is a frozen window for the
    /// length of the scan, which is the one thing §5.5 asks a scanner not to be. On a pool thread it
    /// is a thread held for as long, which the pool has too few of (<see cref="WorkerThreads"/>).
    /// </summary>
    private static Task<T> OnWorker<T>(Func<T> step, CancellationToken ct) => WorkerThreads.Shared.RunAsync(step, ct);

    /// <summary>
    /// The volume <paramref name="root"/> is on, with its table opened, or the reason the walk answers
    /// alone: the path is on no volume this process can address, Settings asks for the walk, or the
    /// table would not open.
    ///
    /// <para>A drive letter mapped to a share parses as a volume and is not one. Opening it raw is a
    /// request to the redirector, which can answer access denied, and the page would then offer to
    /// read a file table as administrator that no share has. So it is asked about before the
    /// letter, and answers as the share it is.</para>
    /// </summary>
    private Opened Open(string root)
    {
        if (!VolumePath.TryParse(root, out var volume) || _tuning.IsOnAShare(root))
        {
            return new Opened(volume, null, FallbackReason.VolumeNotAddressable);
        }

        // Each scan is a look at the machine of its own, so a drive attached since the last one is
        // asked what it is rather than taken for the drive that last had its letter.
        _tuning.Invalidate();

        if (_tuning.WalkOnly)
        {
            return new Opened(volume, null, FallbackReason.WalkChosen);
        }

        var source = _sources.TryOpenTable(volume.DriveLetter, out var reason);

        return new Opened(volume, source, reason);
    }

    /// <summary>A scan's volume and its opened table, or why there is no table to read.</summary>
    private readonly record struct Opened(VolumePath Volume, IMftSource? Source, FallbackReason Reason);

    /// <summary>
    /// The scan the table answered with, or null and why it declined: it read and could not root a
    /// tree where the scan was pointed, or a read failed part way through.
    /// </summary>
    private (ExploreScan? Scan, FallbackReason Declined) ReadTable(
        IMftSource source,
        VolumePath volume,
        IProgress<ExploreProgress>? progress,
        SnapshotAssembly? snapshots,
        CancellationToken ct)
    {
        try
        {
            var read = Read(source, volume, _tuning.ForVolume(volume.DriveLetter).Table, progress, snapshots, ct);

            // Where the table could not root a tree, whether that is a route lost or a route that
            // never existed is the reader's judgement, not this one's — a folder reached through a
            // junction has no record whose subtree is its content, and offering administrator rights
            // for that would be an apology for a choice nobody made.
            return read.Tree is { } tree
                ? (ExploreScan.Fast(tree, fromIncompleteTable: !read.EveryRecordRead), FallbackReason.None)
                : (null, read.Reason);
        }
        catch (IOException)
        {
            // The volume went away mid-scan, or the driver refused a read. Neither should take the
            // window down, and the walk still answers.
            return (null, FallbackReason.MasterFileTableIncomplete);
        }
    }

    /// <summary>
    /// Walk the folder at <paramref name="root"/> while its volume's table is read, and take whichever
    /// answers first. See <see cref="ScanAsync"/>.
    ///
    /// <para>The walk answers only where it listed the folder itself. One it could not list, or that
    /// is not there, has said nothing the table cannot say better, so the table is waited for, as
    /// <see cref="RouteRace"/> says. The walk's tree is taken only if the table declines too, with the
    /// table's reason, as a walk after a declined read carries it.</para>
    ///
    /// <para>While both run the walk is what the caller is told about (<see cref="HeldTableReports"/>).
    /// A snapshot it drew is replaced by the table's tree where the table answers first, as any
    /// snapshot is replaced by the finished tree.</para>
    ///
    /// <para>The table reads at normal priority, unlike an index build raced for a measurement
    /// (<see cref="Scanning.Mft.BuildPriorities"/>). That race is over a dozen small paths. This one may
    /// be over a folder large enough that the table is the quicker route, and a low-priority read of
    /// the table was measured at three to seven times as long as a normal one even with nothing else
    /// reading the disk, which would make a large folder slower to scan than it is without the
    /// race.</para>
    /// </summary>
    private async Task<ExploreScan> RaceAsync(
        string root,
        VolumePath volume,
        IMftSource source,
        IProgress<ExploreProgress>? progress,
        SnapshotAssembly snapshots,
        CancellationToken ct)
    {
        var held = progress is null ? null : new HeldTableReports(progress, snapshots);
        var declined = FallbackReason.None;

        var raced = await RouteRace.FirstAsync(
            async stop =>
            {
                var read = await OnWorker(() => ReadTable(source, volume, held, held?.Snapshots, stop), stop).ConfigureAwait(false);
                declined = read.Declined;
                return read.Scan;
            },
            stop => OnWorker(() => Walk(root, FallbackReason.WalkAnsweredFirst, progress, snapshots, stop), stop),
            walked => !walked.Tree.ListingWasRefused(walked.Tree.RootNode),
            () => held?.TableAlone(),
            ct).ConfigureAwait(false);

        // The table has ended by the time it declines, so its reason has been written.
        return raced.Outcome is RaceOutcome.TableDeclined
            ? ExploreScan.Walked(raced.Answer.Tree, declined)
            : raced.Answer;
    }

    private MftExploreRead Read(
        IMftSource source,
        VolumePath volume,
        TableTuning tuning,
        IProgress<ExploreProgress>? progress,
        SnapshotAssembly? snapshots,
        CancellationToken ct) =>
        // Both halves come from the same parse. The tree names its root with the path the user
        // reads and locates that root by the components, so the two have to describe one location
        // in one form — see VolumePath.FullPath.
        //
        // The scan's own clock, so the walk and the table space their snapshots alike.
        MftExploreReader.Read(
            source, volume.FullPath, volume.Components, tuning, progress, ct, snapshots, _time);

    private ExploreScan Walk(
        string root,
        FallbackReason reason,
        IProgress<ExploreProgress>? progress,
        SnapshotAssembly? snapshots,
        CancellationToken ct,
        LeavesUnlisted? unlisted = null)
    {
        // The scan's own clock, so the walk's reports and its snapshots keep one time.
        var tree = Walker(root, _tuning, _occupancy, progress, _time, ct, unlisted, snapshots);

        return ExploreScan.Walked(tree, reason);
    }
}
