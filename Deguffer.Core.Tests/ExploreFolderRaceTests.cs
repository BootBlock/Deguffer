using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// An Explore scan of a folder under <see cref="ScanRoute.Auto"/>: the folder is walked while its
/// volume's table is read, and whichever answers first is drawn. A scan of a volume's root still
/// reads the table alone, because the walk it would race is the whole volume.
///
/// <para>Which route answers is never left to timing. The table is held with
/// <see cref="HeldMftSourceFactory"/>, whose reads wait until the test releases them, and the walk is
/// a <see cref="StandInWalk"/>, which answers, refuses or waits to be stopped as each test says.</para>
/// </summary>
public sealed class ExploreFolderRaceTests
{
    private const char Letter = DriveLetters.Faked;

    private static readonly string Folder = $@"{Letter}:\Users\testuser\.npm-cache";

    /// <summary>Far longer than any race here takes, and short enough to fail a scan that waits.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>A scan's settings with <paramref name="route"/>, and no device of this machine asked about.</summary>
    internal static ScanTuner Tuner(ScanRoute route) => new(
        new FakePreferences(AppPreferences.Default with { Scanning = ScanPreferences.Default with { Route = route } }),
        new VolumeMediaCache(new FakeStorageQueries()),
        new FakeVolumeInventory());

    /// <summary>
    /// The walk lists the folder while the table is still being read, so the walk's tree is drawn and
    /// nothing is said about it: nothing was unavailable, and the table opened, so the rights are held.
    ///
    /// <para>The table is never released before the scan returns, and the race waits for its loser,
    /// so the scan returns within the bound only where the read was stopped. A scan that waited for the
    /// table's answer would fail on the bound. Its volume is closed by the time the scan returns.</para>
    ///
    /// <para>The table has no bitmap. Its read is a short synchronous one that stops for nothing,
    /// which on a volume is over in moments, and held here would never end. Without it the hold falls
    /// on the pass over the records, which stops when it is told to.</para>
    /// </summary>
    [Fact]
    public async Task AutoTakesTheWalksTreeWhereTheWalkAnswersFirst()
    {
        var sources = new HeldMftSourceFactory(Letter, ExploreScannerTests.Volume().WithoutBitmap());
        var walk = new StandInWalk((root, _, _) => Listed(root));

        try
        {
            var scan = await Scanner(sources, walk).ScanAsync(Folder).AsTask().WaitAsync(Bound);

            Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
            Assert.Equal(FallbackReason.WalkAnsweredFirst, scan.Fallback);
            Assert.Null(scan.RouteNote);
            Assert.False(ElevationOffer.ShouldOffer(isElevated: false, scan.Fallback, HiddenSpace.None));
            Assert.False(scan.FromIncompleteTable);
            Assert.NotNull(ExploreTreePaths.TryNodeAt(scan.Tree, $@"{Folder}\walked.bin"));
            Assert.Equal(1, sources.CloseCount);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A walk that could not list the folder has said nothing the table cannot say better, so the
    /// table is waited for and its tree drawn. The table is released only once the walk has ended,
    /// so the walk was over before the table had read anything.
    /// </summary>
    [Fact]
    public async Task AutoWaitsForTheTableWhereTheWalkCouldNotListTheFolder()
    {
        var sources = new HeldMftSourceFactory(Letter, ExploreScannerTests.Volume());
        var walk = new StandInWalk((root, _, _) => Refused(root));

        var scanning = Scanner(sources, walk).ScanAsync(Folder).AsTask();
        await walk.Ended.WaitAsync(Bound);
        sources.Release();
        var scan = await scanning.WaitAsync(Bound);

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Equal(FallbackReason.None, scan.Fallback);
        Assert.Equal(8000, scan.Tree.TotalLength);
        Assert.Equal([Folder], walk.Walked);
    }

    /// <summary>
    /// Where the table then does not describe the folder either, the walk's tree stands, with the
    /// reason the table gave rather than the claim that the walk was merely quicker, as a walk after a
    /// declined read carries it.
    /// </summary>
    [Fact]
    public async Task AutoSaysWhyTheTableDeclinedWhereTheWalkCouldNotListTheFolder()
    {
        var missing = $@"{Letter}:\Users\testuser\.pnpm-store";
        var sources = FakeMftSourceFactory.Serving(Letter, ExploreScannerTests.Volume());
        var walk = new StandInWalk((root, _, _) => Refused(root));

        var scan = await Scanner(sources, walk).ScanAsync(missing).AsTask().WaitAsync(Bound);

        Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
        Assert.Equal(FallbackReason.MasterFileTableIncomplete, scan.Fallback);
        Assert.Contains("file table", scan.RouteNote!, StringComparison.OrdinalIgnoreCase);
        Assert.True(scan.Tree.ListingWasRefused(scan.Tree.RootNode));
        Assert.Equal([missing], walk.Walked);
    }

    /// <summary>
    /// The table answers while the walk is still listing, so the table's tree is drawn, and the walk is
    /// stopped and has ended before the scan returns, so nothing walks the disk or reports behind a
    /// finished scan.
    /// </summary>
    [Fact]
    public async Task AutoTakesTheTablesTreeWhereTheTableAnswersFirst()
    {
        var sources = FakeMftSourceFactory.Serving(Letter, ExploreScannerTests.Volume());
        var walk = new StandInWalk((_, ct, _) => UntilStopped(ct));

        var scan = await Scanner(sources, walk).ScanAsync(Folder).AsTask().WaitAsync(Bound);

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Equal(FallbackReason.None, scan.Fallback);
        Assert.Equal(8000, scan.Tree.TotalLength);
        Assert.Equal([Folder], walk.Walked);
        Assert.True(walk.Ended.IsCompleted, "The scan returned with its walk still running.");
    }

    /// <summary>
    /// A volume's root is read from the table alone under Auto: the walk it would race is the whole
    /// volume, which §5.5 measured at over ten minutes.
    /// </summary>
    [Fact]
    public async Task AutoReadsOnlyTheTableForAVolumeRoot()
    {
        var sources = FakeMftSourceFactory.Serving(Letter, ExploreScannerTests.Volume());
        var walk = new StandInWalk((root, _, _) => Listed(root));

        var scan = await Scanner(sources, walk).ScanAsync($@"{Letter}:\").AsTask().WaitAsync(Bound);

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Empty(walk.Walked);
    }

    /// <summary>
    /// Table waits for the table for a folder too, and walks nothing beside it. The table is held long
    /// enough that a raced walk would already have answered.
    /// </summary>
    [Fact]
    public async Task TableReadsOnlyTheTableForAFolder()
    {
        var sources = new HeldMftSourceFactory(Letter, ExploreScannerTests.Volume());
        var walk = new StandInWalk((root, _, _) => Listed(root));
        var scanner = new ExploreScanner(sources, tuning: Tuner(ScanRoute.Table)) { Walker = walk.Walk };

        var scanning = scanner.ScanAsync(Folder).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        sources.Release();
        var scan = await scanning.WaitAsync(Bound);

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Empty(walk.Walked);
    }

    /// <summary>
    /// While both routes run, the page is told about the walk alone. The walk counts towards no total
    /// and the table towards its record count, so a page told both would flip between the two on every
    /// report. The table is told nothing and assembles no snapshot, though the clock moves a whole
    /// snapshot interval on every report the table makes.
    /// </summary>
    [Fact]
    public async Task WhileBothRoutesRunOnlyTheWalkIsReported()
    {
        var sources = FakeMftSourceFactory.Serving(Letter, ExploreScannerTests.LongVolume());
        var walk = new StandInWalk((_, ct, progress) =>
        {
            progress!.Report(new ExploreProgress(1, Total: null, BytesSeen: 10));
            return UntilStopped(ct);
        });
        var (scanner, progress, reports, assembled) = Timed(sources, walk);

        var scan = await scanner.ScanAsync(Folder, progress).AsTask().WaitAsync(Bound);

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Equal(new ExploreProgress(1, Total: null, BytesSeen: 10), Assert.Single(reports));
        Assert.Equal(0, assembled());
    }

    /// <summary>
    /// Once the walk has ended without listing the folder, the table is the only route left answering,
    /// and the page is told its counts and shown its snapshots from then on.
    /// </summary>
    [Fact]
    public async Task OnceTheWalkEndsWithoutAnAnswerTheTableIsReported()
    {
        var sources = new HeldMftSourceFactory(Letter, ExploreScannerTests.LongVolume());
        var walk = new StandInWalk((root, _, progress) =>
        {
            progress!.Report(new ExploreProgress(1, Total: null, BytesSeen: 10));
            return Refused(root);
        });
        var (scanner, progress, reports, _) = Timed(sources, walk);

        var scanning = scanner.ScanAsync(Folder, progress).AsTask();
        await walk.Ended.WaitAsync(Bound);
        sources.Release();
        var scan = await scanning.WaitAsync(Bound);

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Null(reports[0].Total);
        Assert.All(reports.Skip(1), report => Assert.NotNull(report.Total));
        Assert.True(reports.Count > 2, $"The table made {reports.Count - 1} reports after the walk ended.");
        Assert.Contains(reports, report => report.Snapshot is not null);
    }

    private static ExploreScanner Scanner(IMftSourceFactory sources, StandInWalk walk) =>
        new(sources, tuning: Tuner(ScanRoute.Auto)) { Walker = walk.Walk };

    /// <summary>
    /// A raced scanner that assembles each snapshot on the spot and counts it, and a progress that
    /// records each report and moves the scanner's clock a whole snapshot interval, so a snapshot is
    /// due at every report a route makes.
    /// </summary>
    private static (ExploreScanner Scanner, IProgress<ExploreProgress> Progress, List<ExploreProgress> Reports, Func<int> Assembled) Timed(
        IMftSourceFactory sources, StandInWalk walk)
    {
        var clock = new ManualTimeProvider();
        var reports = new List<ExploreProgress>();
        var assembled = 0;

        var progress = new CallbackProgress<ExploreProgress>(report =>
        {
            lock (reports)
            {
                reports.Add(report);
            }

            clock.Advance(ScanSnapshots.Interval);
        });

        var scanner = new ExploreScanner(sources, clock, Tuner(ScanRoute.Auto))
        {
            Walker = walk.Walk,
            AssembleSnapshot = assemble =>
            {
                Interlocked.Increment(ref assembled);
                assemble();
                return Task.CompletedTask;
            },
        };

        return (scanner, progress, reports, () => Volatile.Read(ref assembled));
    }

    /// <summary>A walk that listed the folder, holding one file of its own, so its tree is told from the table's.</summary>
    private static ExploreTree Listed(string root)
    {
        var builder = new ExploreTreeBuilder(LongPath.Display(root));
        builder.AddChildren(ExploreTreeBuilder.RootNode, [new ExploreChild("walked.bin", IsDirectory: false, IsLink: false, 4096)]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>A walk that could not list the folder, as one refused it or one where it is not there.</summary>
    private static ExploreTree Refused(string root)
    {
        var builder = new ExploreTreeBuilder(LongPath.Display(root));
        builder.MarkListingRefused(ExploreTreeBuilder.RootNode);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>A walk still listing until it is stopped, which then stops as a cancelled walk does.</summary>
    private static ExploreTree UntilStopped(CancellationToken ct)
    {
        ct.WaitHandle.WaitOne(Bound);
        ct.ThrowIfCancellationRequested();

        throw new TimeoutException("The walk was never stopped.");
    }

    /// <summary>
    /// Stands in for the walk: does what the test gives it, notes each folder it was asked of, and
    /// says when it has ended, however it ended.
    /// </summary>
    private sealed class StandInWalk(Func<string, CancellationToken, IProgress<ExploreProgress>?, ExploreTree> walk)
    {
        private readonly Lock _gate = new();
        private readonly List<string> _walked = [];
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Walked
        {
            get
            {
                lock (_gate)
                {
                    return [.. _walked];
                }
            }
        }

        /// <summary>Completes once the walk has returned or thrown.</summary>
        public Task Ended => _ended.Task;

        public ExploreTree Walk(
            string root,
            ScanTuner tuning,
            IOccupancyProbe probe,
            IProgress<ExploreProgress>? progress,
            TimeProvider clock,
            CancellationToken ct,
            LeavesUnlisted? unlisted,
            SnapshotAssembly? snapshotAssembly)
        {
            lock (_gate)
            {
                _walked.Add(root);
            }

            try
            {
                return walk(root, ct, progress);
            }
            finally
            {
                _ended.TrySetResult();
            }
        }
    }
}
