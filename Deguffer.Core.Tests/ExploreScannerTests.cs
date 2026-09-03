using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The route choice for a whole volume, or for one folder on it: which of §5.5's two strategies
/// runs, and what the user is told about it.
///
/// <para>The distinction this exists to pin is that the walk is reached two ways. Usually something
/// was unavailable and the user can be offered a remedy; sometimes the walk is simply the right
/// route, and offering administrator rights that would change nothing is an apology for a choice
/// nobody made. Both arrive as <see cref="ScanStrategy.ParallelEnumeration"/>, so only the reason
/// and the note separate them.</para>
/// </summary>
public class ExploreScannerTests
{
    // A synthetic profile tree. Paths are invented rather than copied from a real machine.
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Cache = 8;

    private const uint Sibling = 9;

    private static MftFixture Volume() => new MftFixture()
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser")
        .AddDirectory(Cache, Profile, ".npm-cache")
        .AddDirectory(Sibling, Profile, ".config")
        .AddFile(20, Cache, "a.tgz", allocated: 8192, logical: 8000)

        // Outside the folder the scoped tests point at, so their totals say which subtree was read
        // rather than agreeing with the whole volume's by accident.
        .AddFile(21, Sibling, "settings.json", allocated: 1024, logical: 1000);

    [Fact]
    public async Task ReadsTheTableForAWholeVolumeWhereItCan()
    {
        var letter = UnusedDriveLetter();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Serving(letter, Volume()));

        var scan = await scanner.ScanAsync($@"{letter}:\");

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Equal(FallbackReason.None, scan.Fallback);
        Assert.Null(scan.RouteNote);
        Assert.Equal(9000, scan.Tree.TotalBytes);
        Assert.Equal($@"{letter}:\Users\testuser\.npm-cache\a.tgz", scan.Tree.PathOf(20));
    }

    /// <summary>
    /// §6.3 says the app runs unelevated by default, so this is the ordinary path rather than an
    /// edge case — and §5.5 says the fallback must be observable. A silent slow scan looks exactly
    /// like a large disk, and the user is never told that elevating would make it quick.
    /// </summary>
    [Fact]
    public async Task FallsBackToTheWalkAndOffersElevationWhenTheTableCannotBeOpened()
    {
        var letter = UnusedDriveLetter();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

        var scan = await scanner.ScanAsync($@"{letter}:\");

        Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
        Assert.Equal(FallbackReason.NotElevated, scan.Fallback);
        Assert.Contains("administrator", scan.RouteNote!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A scan scoped to a folder reads the table too, rooted at the record that holds the folder.
    /// The one pass the route costs answers for a folder exactly as it answers for a drive, and the
    /// walk it replaces is the one §5.5 measured at over ten minutes.
    /// </summary>
    [Fact]
    public async Task ReadsTheTableForAFolderBelowTheVolumeRoot()
    {
        var letter = UnusedDriveLetter();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Serving(letter, Volume()));

        var scan = await scanner.ScanAsync($@"{letter}:\Users\testuser\.npm-cache");

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Equal(FallbackReason.None, scan.Fallback);
        Assert.Null(scan.RouteNote);
        // 8000, not the volume's 9000: the sibling above the scope is outside what was read.
        Assert.Equal(8000, scan.Tree.TotalBytes);
        Assert.Equal($@"{letter}:\Users\testuser\.npm-cache", scan.Tree.RootPath);
        Assert.Equal($@"{letter}:\Users\testuser\.npm-cache\a.tgz", scan.Tree.PathOf(20));
    }

    /// <summary>
    /// §6.3: every path in Core goes through <c>LongPath</c>, so a root can arrive
    /// here in extended-length form — and the tree has to name itself in the form a person reads and
    /// a shell opens.
    ///
    /// <para>The form is what is asserted rather than the depth, because a deep tree cannot fail:
    /// .NET prepends the prefix itself past 260 characters. What discriminates is that the root the
    /// tree reports, and every path it rebuilds below it, is the one the user would recognise —
    /// which is also the invariant the file-table route now depends on, since it locates the folder
    /// by the components of the same parse that produced this string.</para>
    /// </summary>
    [Fact]
    public async Task NamesAFolderInTheFormAPersonReadsWhateverFormItWasGiven()
    {
        var letter = UnusedDriveLetter();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Serving(letter, Volume()));

        var scan = await scanner.ScanAsync($@"\\?\{letter}:\Users\testuser\.npm-cache");

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Equal($@"{letter}:\Users\testuser\.npm-cache", scan.Tree.RootPath);
        Assert.Equal($@"{letter}:\Users\testuser\.npm-cache\a.tgz", scan.Tree.PathOf(20));
    }

    /// <summary>
    /// A folder scan that could have used the table and did not is a fallback like any other, so it
    /// says so and offers the rights that would change the answer.
    ///
    /// <para>The open is asserted because it is what separates this answer from the one below,
    /// where nothing was lost: a volume that was never opened cannot have had a route taken from
    /// it.</para>
    /// </summary>
    [Fact]
    public async Task OffersElevationForAFolderScanThatCouldHaveReadTheTable()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(4096, "cache", "a.bin");

        var sources = FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated);
        var scan = await new ExploreScanner(sources).ScanAsync(Path.Combine(temp.Path, "cache"));

        Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
        Assert.Equal(FallbackReason.NotElevated, scan.Fallback);
        Assert.Contains("administrator", scan.RouteNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, sources.OpenCount);
        Assert.Equal(4096, scan.Tree.TotalBytes);
    }

    /// <summary>
    /// The walked scan that still carries <see cref="FallbackReason.None"/> and no note at all: a
    /// folder reached through a junction, which the table can never root at because whatever the
    /// link stands for keeps its own place under its real parent.
    ///
    /// <para>Nothing was lost here, so nothing is offered. Administrator rights would not change the
    /// answer, and saying a route was unavailable would be an apology for a choice nobody made. The
    /// table is served and opened, which is what makes this the deliberate answer rather than the
    /// one a missing volume produces.</para>
    /// </summary>
    [Fact]
    public async Task WalksAFolderReachedThroughALinkWithoutOfferingARouteThatWasNeverLost()
    {
        var letter = UnusedDriveLetter();
        var sources = FakeMftSourceFactory.Serving(
            letter, Volume().AddDirectoryLink(30, Profile, "linked-cache"));

        var scan = await new ExploreScanner(sources).ScanAsync($@"{letter}:\Users\testuser\linked-cache");

        Assert.Equal(1, sources.OpenCount);
        Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
        Assert.Equal(FallbackReason.None, scan.Fallback);
        Assert.Null(scan.RouteNote);
    }

    /// <summary>
    /// A folder the table read and does not describe is a route that was lost, so the walk answers
    /// and the note says which. Distinct from the junction above, where there was never a route to
    /// lose.
    /// </summary>
    [Fact]
    public async Task ReportsAFolderTheTableDoesNotDescribe()
    {
        var letter = UnusedDriveLetter();
        var sources = FakeMftSourceFactory.Serving(letter, Volume());

        var scan = await new ExploreScanner(sources).ScanAsync($@"{letter}:\Users\testuser\.pnpm-store");

        Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
        Assert.Equal(FallbackReason.MasterFileTableIncomplete, scan.Fallback);
        Assert.Contains("file table", scan.RouteNote!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A UNC path has no volume this process can open, so there is no table to read and nothing the
    /// user can do about it. It still has to be scanned rather than refused.
    /// </summary>
    [Fact]
    public async Task ReportsAPathThatIsOnNoLocalVolumeItCanAddress()
    {
        var sources = FakeMftSourceFactory.Serving('C', Volume());
        var scan = await new ExploreScanner(sources).ScanAsync(@"\\deguffer.test\share\cache");

        Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
        Assert.Equal(FallbackReason.VolumeNotAddressable, scan.Fallback);
        Assert.Contains("local volume", scan.RouteNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, sources.OpenCount);
    }

    /// <summary>
    /// §5.5: never block on a complete scan. The walk cannot say how many directories it has yet to
    /// open, so its reports are indeterminate by design — but they have to arrive, and the counts
    /// have to rise, or the window shows a scan that is running and never getting anywhere.
    /// </summary>
    [Fact]
    public async Task ReportsRisingIndeterminateProgressWhileItWalks()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1000, "cache", "a.bin");
        temp.CreateFile(2000, "cache", "one", "b.bin");
        temp.CreateFile(4000, "cache", "one", "two", "c.bin");

        var progress = new ProgressRecorder<ExploreProgress>();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

        await scanner.ScanAsync(Path.Combine(temp.Path, "cache"), progress);

        Assert.NotEmpty(progress.Reports);
        Assert.All(progress.Reports, report =>
        {
            Assert.Null(report.Total);
            Assert.Null(report.Fraction);
        });

        Assert.Equal(5, progress.Reports[^1].Done);
        Assert.Equal(7000, progress.Reports[^1].BytesSeen);
    }

    /// <summary>
    /// The table route drives a real progress bar, because the table states its own record count up
    /// front. That is the whole difference from the walk, and a fraction that never resolves to a
    /// number is a bar that cannot be drawn.
    /// </summary>
    [Fact]
    public async Task ReportsAMeasuredFractionWhileItReadsTheTable()
    {
        var letter = UnusedDriveLetter();
        var progress = new ProgressRecorder<ExploreProgress>();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Serving(letter, Volume().AddUnused(70_000)));

        await scanner.ScanAsync($@"{letter}:\", progress);

        Assert.NotEmpty(progress.Reports);
        Assert.All(progress.Reports, report => Assert.Equal(70_001, report.Total));
        Assert.Contains(progress.Reports, report => report.Fraction > 0);
    }

    /// <summary>
    /// A tree to draw, handed over while the walk is still running. §5.5 asks for streamed partial
    /// results, and a snapshot is what "partial" means for a picture — a running total says a scan is
    /// progressing, but gives the view nothing to put on screen.
    ///
    /// <para>The cadence is a wall-clock interval rather than a level count, deliberately: copying
    /// every array is not free, and a scan of a full drive is long enough that an unchanging window
    /// reads as a hung one. So what the test has to produce is elapsed time, and it produces it by
    /// holding the reporting thread rather than by building a fixture large enough to take three
    /// quarters of a second to walk — which at unit scale would mean tens of thousands of files, and
    /// would still be timing-dependent. Holding the thread exercises the same decision: no snapshot
    /// while the interval has not passed, one when it has.</para>
    /// </summary>
    [Fact]
    public async Task PublishesATreeToDrawOnlyOnceTheSnapshotIntervalHasPassed()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1000, "cache", "a.bin");
        temp.CreateFile(2000, "cache", "one", "b.bin");
        temp.CreateFile(4000, "cache", "one", "two", "c.bin");
        temp.CreateFile(8000, "cache", "one", "two", "three", "d.bin");

        var reports = new List<ExploreProgress>();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

        // Half the interval per level, so the third report is the first one past it.
        var progress = new CallbackProgress<ExploreProgress>(report =>
        {
            reports.Add(report);

            if (reports.Count <= 2)
            {
                Thread.Sleep(400);
            }
        });

        var scan = await scanner.ScanAsync(Path.Combine(temp.Path, "cache"), progress);

        Assert.Null(reports[0].Snapshot);
        Assert.Null(reports[1].Snapshot);

        var published = reports.Select(report => report.Snapshot).OfType<ExploreTree>().ToList();

        Assert.NotEmpty(published);
        Assert.True(
            published[0].NodeCount < scan.Tree.NodeCount,
            "The first snapshot already held the whole tree, so nothing was published early.");
        Assert.True(published[0].NodeCount > 1, "The snapshot held nothing but the root.");
    }

    /// <summary>
    /// A snapshot is ordered by name and the finished tree by size, which is the whole of how the
    /// map is kept still while a scan runs. The sizes in a partial tree are still growing, so
    /// ordering siblings by one of them makes every snapshot a different arrangement of the same
    /// disk; a name does not grow.
    ///
    /// <para>Driven the same way as the interval test above, by holding the reporting thread, for
    /// the same reason: what is needed is elapsed time rather than a fixture large enough to take
    /// three quarters of a second to walk.</para>
    /// </summary>
    [Fact]
    public async Task OrdersASnapshotByNameAndTheFinishedTreeBySize()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1000, "cache", "a.bin");
        temp.CreateFile(2000, "cache", "one", "b.bin");
        temp.CreateFile(4000, "cache", "one", "two", "c.bin");

        var reports = new List<ExploreProgress>();
        var progress = new CallbackProgress<ExploreProgress>(report =>
        {
            reports.Add(report);
            Thread.Sleep(400);
        });

        var scanner = new ExploreScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));
        var scan = await scanner.ScanAsync(Path.Combine(temp.Path, "cache"), progress);

        var published = reports.Select(report => report.Snapshot).OfType<ExploreTree>().ToList();

        Assert.NotEmpty(published);
        Assert.All(published, tree => Assert.Equal(ExploreChildOrder.ByName, tree.ChildOrder));
        Assert.Equal(ExploreChildOrder.BySize, scan.Tree.ChildOrder);
    }

    /// <summary>
    /// G4: a scan the user cannot abandon is a bug, and it is a bug on both routes. Neither is
    /// interruptible in itself — one is a pass over millions of records, the other a level-by-level
    /// walk — so the token has to reach the loop that drives each of them.
    ///
    /// <para>Cancelled from inside a progress report, so what is under test is a scan already under
    /// way. A token cancelled beforehand proves only that <see cref="Task.Run(Action,
    /// CancellationToken)"/> checks it before starting, which says nothing about either route.</para>
    /// </summary>
    [Fact]
    public async Task StopsTheTableReadWhenTheScanIsCancelled()
    {
        var letter = UnusedDriveLetter();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Serving(letter, Volume().AddUnused(70_000)));

        using var cancel = new CancellationTokenSource();
        var progress = new CallbackProgress<ExploreProgress>(_ => cancel.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync($@"{letter}:\", progress, cancel.Token).AsTask());
    }

    [Fact]
    public async Task StopsTheWalkWhenTheScanIsCancelled()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(64, "cache", "one", "two", "a.bin");

        var scanner = new ExploreScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

        using var cancel = new CancellationTokenSource();
        var progress = new CallbackProgress<ExploreProgress>(_ => cancel.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(Path.Combine(temp.Path, "cache"), progress, cancel.Token).AsTask());
    }

    /// <summary>
    /// A drive letter no volume answers to, so a scan rooted at it reaches the route choice and then
    /// finds nothing to walk. Without it, testing the volume-root branch would mean walking a real
    /// disk.
    /// </summary>
    private static char UnusedDriveLetter() =>
        "ZYXWVUTSRQPONM".First(letter => !Directory.Exists($@"{letter}:\"));

    /// <summary>
    /// Progress that runs the callback on the reporting thread. <see cref="Progress{T}"/> posts to
    /// the thread pool, so a test that has to act on a report while the scan is still on it cannot
    /// use one.
    /// </summary>
    private sealed class CallbackProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
