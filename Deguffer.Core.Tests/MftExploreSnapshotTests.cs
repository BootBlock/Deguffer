using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The trees a read of the file table hands over while it is still reading (#492).
///
/// <para>Each snapshot is assembled on the spot, inside the progress report that starts it, so it
/// holds exactly the records the read had dealt with at that report, and the clock moves a whole
/// snapshot interval on every report, so every report from the second starts one. The table is read
/// with one read in flight and one parse thread, as <see cref="TableTuning.Default"/> reads it, and
/// reports once per <see cref="MftPassProgress.Interval"/> records: the first snapshot is started by
/// the report at that many records and handed over by the next.</para>
/// </summary>
public sealed class MftExploreSnapshotTests
{
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Cache = 8;
    private const uint Sibling = 10;

    // Past the first report, so the first snapshot is taken before this folder is read, while the
    // file below it, at a low record number, already has been.
    private const uint Late = MftPassProgress.Interval + 5;

    private const string Root = @"C:\";
    private const string ProfilePath = @"C:\Users\testuser";
    private const string CachePath = ProfilePath + @"\.npm-cache";
    private const string SiblingPath = ProfilePath + @"\.config";
    private const string LatePath = ProfilePath + @"\late";

    private static readonly Func<Action, Task> OnTheSpot = assemble =>
    {
        assemble();
        return Task.CompletedTask;
    };

    /// <summary>
    /// A snapshot asked for by size, as one is while the treemap is on screen, comes by size, and is
    /// still marked as a snapshot with its totals as lower bounds; the finished tree is not marked.
    /// </summary>
    [Fact]
    public void HandsOverASnapshotInTheOrderItWasAskedFor()
    {
        using var source = Table().Build();

        var (reports, read) = ReadWithSnapshots(source, Root, [], order: ExploreChildOrder.BySize);
        var snapshot = FirstSnapshot(reports);

        Assert.Equal(ExploreChildOrder.BySize, snapshot.ChildOrder);
        Assert.True(snapshot.IsSnapshot);
        Assert.True(snapshot.HasUnknownSizeBelow(snapshot.RootNode), "The snapshot gave its totals as exact.");

        // By size, where name would put .config first.
        var profile = At(snapshot, ProfilePath);
        Assert.Equal(
            [".npm-cache", ".config"],
            snapshot.ChildrenOf(profile).ToArray().Select(child => snapshot.NameOf(child)));

        Assert.False(read.Tree!.IsSnapshot);
    }

    /// <summary>
    /// A snapshot holds every record whose chain of parents was read, and nothing else: a file read
    /// before its folder hangs from nothing yet, and a hard link's other names are placed only once
    /// the read ends. Its root says every total is a lower bound, and its folders are in name order,
    /// so a folder that grows stays where it is. The finished tree is the one a read with no
    /// snapshots builds.
    /// </summary>
    [Fact]
    public void HandsOverATreeOfWhatIsReachableSoFarWhileTheTableIsRead()
    {
        using var source = Table().Build();

        var (reports, read) = ReadWithSnapshots(source, Root, []);
        var snapshot = FirstSnapshot(reports);

        Assert.Equal(ExploreChildOrder.ByName, snapshot.ChildOrder);
        Assert.True(snapshot.IsSnapshot);
        Assert.Equal(Root, snapshot.RootPath);
        Assert.True(snapshot.HasUnknownSizeBelow(snapshot.RootNode), "The snapshot gave its totals as exact.");

        Assert.Equal(4096 + 2048, snapshot.SizeOf(At(snapshot, CachePath)));
        Assert.NotNull(ExploreTreePaths.TryNodeAt(snapshot, CachePath + @"\linked.dll"));
        Assert.Null(ExploreTreePaths.TryNodeAt(snapshot, SiblingPath + @"\linked.dll"));
        Assert.Null(ExploreTreePaths.TryNodeAt(snapshot, LatePath));
        Assert.DoesNotContain("waiting.bin", Names(snapshot));

        // By name, where size would put the larger .npm-cache first.
        var profile = At(snapshot, ProfilePath);
        Assert.Equal(
            [".config", ".npm-cache"],
            snapshot.ChildrenOf(profile).ToArray().Select(child => snapshot.NameOf(child)));

        using var again = Table().Build();
        var unsnapped = MftExploreReader.Read(again, Root, [], TableTuning.Default, progress: null, default).Tree!;
        var finished = read.Tree!;

        Assert.Equal(ExploreChildOrder.BySize, finished.ChildOrder);
        Assert.False(finished.HasUnknownSizeBelow(finished.RootNode));
        Assert.Equal(Paths(unsnapped), Paths(finished));
        Assert.Equal(unsnapped.TotalBytes, finished.TotalBytes);
        Assert.Equal(8192, finished.SizeOf(At(finished, LatePath)));
        Assert.NotNull(ExploreTreePaths.TryNodeAt(finished, SiblingPath + @"\linked.dll"));
    }

    /// <summary>
    /// A scan rooted at a folder is drawn from that folder as soon as the read has reached it and
    /// every folder above it, and carries the path the user chose, as the finished tree does.
    /// </summary>
    [Fact]
    public void RootsASnapshotAtTheFolderTheScanWasPointedAt()
    {
        using var source = Table().Build();

        var (reports, _) = ReadWithSnapshots(source, CachePath, ["Users", "testuser", ".npm-cache"]);
        var snapshot = FirstSnapshot(reports);

        Assert.Equal(CachePath, snapshot.RootPath);
        Assert.True(snapshot.HasUnknownSizeBelow(snapshot.RootNode));
        Assert.Equal(4096 + 2048, snapshot.TotalBytes);
        Assert.Equal(["a.tgz", "linked.dll"], Names(snapshot).Where(name => name != CachePath).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Nothing is drawn of a folder the read has not reached yet. There is no tree to root, and a
    /// tree of the volume above it would be a picture of somewhere the user did not point the scan.
    /// </summary>
    [Fact]
    public void HandsOverNothingBeforeTheReadHasReachedTheFolderTheScanWasPointedAt()
    {
        using var source = Table().Build();

        var (reports, read) = ReadWithSnapshots(source, LatePath, ["Users", "testuser", "late"]);

        Assert.Null(reports[2].Snapshot);
        Assert.Equal(LatePath, read.Tree!.RootPath);
        Assert.Equal(8192, read.Tree.TotalBytes);
    }

    /// <summary>
    /// A path typed in another case than the disk holds is rooted by the finished read, which falls
    /// back to a name differing only in case, and never by a snapshot. Part way through, the folder
    /// named exactly may not have been read yet, and a snapshot rooted at its case-differing
    /// neighbour would draw the wrong folder's content under the right one's name.
    /// </summary>
    [Fact]
    public void RootsNoSnapshotAtAFolderWhoseNameMatchesOnlyWithoutCase()
    {
        using var source = Table().Build();
        const string typed = ProfilePath + @"\.NPM-cache";

        var (reports, read) = ReadWithSnapshots(source, typed, ["Users", "testuser", ".NPM-cache"]);

        Assert.Null(reports[2].Snapshot);
        Assert.Equal(4096 + 2048, read.Tree!.TotalBytes);
    }

    /// <summary>
    /// The report at nought starts no snapshot, although the first is due from the start. It has
    /// dealt with no record, so the pass over every slot it would start draws nothing, and the next
    /// would then wait the snapshot interval at least, or a multiple of that pass on a large table.
    /// The first snapshot is started by the first report that has records behind it, here with no
    /// time passed at all.
    /// </summary>
    [Fact]
    public void StartsTheFirstSnapshotFromTheFirstReportThatHasDealtWithRecords()
    {
        using var source = Table().Build();

        var (reports, _) = ReadWithSnapshots(source, Root, [], perReport: TimeSpan.Zero);

        var snapshot = FirstSnapshot(reports);

        Assert.Equal(4096 + 2048, snapshot.SizeOf(At(snapshot, CachePath)));
    }

    /// <summary>
    /// The report that says the whole table has been dealt with carries no snapshot. The finished
    /// tree is assembled straight after it, so a snapshot then would be a pass over the whole volume
    /// that nobody sees.
    ///
    /// <para>A table of exactly two intervals, so that report is the third, which would otherwise
    /// hand over the snapshot the second started.</para>
    /// </summary>
    [Fact]
    public void TakesNoSnapshotFromTheReportThatTheWholeTableWasRead()
    {
        using var source = Table((2 * MftPassProgress.Interval) - 1).Build();

        var (reports, _) = ReadWithSnapshots(source, Root, []);

        Assert.Equal(source.RecordCount, reports[^1].Done);
        Assert.Null(reports[^1].Snapshot);
    }

    /// <summary>
    /// Reports at nought and at each <see cref="MftPassProgress.Interval"/> records, and at the end
    /// where the end is a whole number of reads. The first snapshot is started by the second report
    /// and handed over by the third.
    /// </summary>
    private static MftFixture Table(uint lastRecord = (2 * MftPassProgress.Interval) + 10) => new MftFixture()
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser")
        .AddDirectory(Cache, Profile, ".npm-cache")
        .AddDirectory(Sibling, Profile, ".config")
        .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4000)
        .AddHardLinkedFile(21, [(Cache, "linked.dll"), (Sibling, "linked.dll")], allocated: 2048, logical: 2000)
        .AddFile(30, Late, "waiting.bin", allocated: 8192, logical: 8000)
        .AddDirectory(Late, Profile, "late")
        .AddUnused(lastRecord)
        .WithoutBitmap();

    private static (List<ExploreProgress> Reports, MftExploreRead Read) ReadWithSnapshots(
        IMftSource source,
        string rootPath,
        string[] components,
        TimeSpan? perReport = null,
        ExploreChildOrder order = ExploreChildOrder.ByName)
    {
        var clock = new ManualTimeProvider();
        var reports = new List<ExploreProgress>();
        var progress = new CallbackProgress<ExploreProgress>(report =>
        {
            reports.Add(report);
            clock.Advance(perReport ?? ScanSnapshots.Interval);
        });

        var read = MftExploreReader.Read(
            source, rootPath, components, TableTuning.Default, progress, default, new SnapshotAssembly(OnTheSpot, () => order), clock);

        Assert.True(
            reports.Count >= 3 && reports[1].Done == MftPassProgress.Interval,
            $"The read made {reports.Count} reports, not one at nought, one at each interval and one at the end.");

        return (reports, read);
    }

    private static ExploreTree FirstSnapshot(List<ExploreProgress> reports)
    {
        Assert.Null(reports[0].Snapshot);
        Assert.Null(reports[1].Snapshot);

        return reports[2].Snapshot ?? throw new Xunit.Sdk.XunitException("The read handed over no snapshot while it ran.");
    }

    private static int At(ExploreTree tree, string path) => TreeNodes.At(tree, path);

    private static IEnumerable<string> Names(ExploreTree tree) =>
        Enumerable.Range(0, tree.NodeCount).Select(tree.NameOf);

    private static string[] Paths(ExploreTree tree) =>
        [.. Enumerable.Range(0, tree.NodeCount).Select(tree.PathOf).Order(StringComparer.Ordinal)];
}
