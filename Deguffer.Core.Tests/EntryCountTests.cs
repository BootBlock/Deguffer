using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// How many entries each of §5.5's routes says a removal would take, and that the two say the same.
///
/// <para>The count decides whether a leftover holding no bytes can be offered at all, so the
/// direction it may be wrong in matters more than its precision: it must never count something the
/// removal leaves standing. Each assertion below says what the removal would take and what it would
/// leave.</para>
/// </summary>
public sealed class EntryCountTests : IDisposable
{
    private static readonly ParallelEnumerationScanner Walk = ParallelEnumerationScanner.Default;

    private static readonly string[] CachePath = ["Users", "testuser", "cache"];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DirectoryScanner Walking() =>
        new(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

    private static DirectoryScanner Indexing(string path, MftFixture fixture) =>
        new(FakeMftSourceFactory.Serving(Path.GetFullPath(path)[0], fixture));

    private static MftFixture Profile() => new MftFixture()
        .AddDirectory(6, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(7, 6, "testuser");

    private static MftVolumeIndex Build(MftFixture fixture)
    {
        using var source = fixture.Build();

        Assert.True(MftVolumeIndexBuilder.TryBuild(source, out var index));
        return index;
    }

    [Fact]
    public void EntriesAddUpAndNeverFallBelowNothing()
    {
        var three = new ScanSize(10, 10, Entries: 3);
        var five = new ScanSize(4, 4, Entries: 5);

        Assert.Equal(8, (three + five).Entries);
        Assert.Equal(0, (three - five).Entries);
        Assert.Equal(2, (five - three).Entries);
    }

    [Fact]
    public async Task TheWalkCountsTheFolderEveryFolderInItAndEveryFile()
    {
        _temp.CreateFile(1000, "cache", "a.bin");
        _temp.CreateFile(2000, "cache", "nested", "b.bin");
        _temp.CreateFile(0, "cache", "nested", "deeper", "empty.tmp");
        _temp.CreateDirectory("cache", "nothing-in-here");

        var result = await Walk.MeasureAsync(Path.Combine(_temp.Path, "cache"));

        // cache, nested, deeper and nothing-in-here, and the three files.
        Assert.Equal(7, result.Size.Entries);
        Assert.Equal(3000, result.Size.Logical);
    }

    /// <summary>The case the count exists for: nothing to reclaim in bytes, and one entry to remove.</summary>
    [Fact]
    public async Task AnEmptyFolderIsOneEntryAndNoBytes()
    {
        var empty = _temp.CreateDirectory("session");

        var result = await Walk.MeasureAsync(empty);

        Assert.Equal(0, result.Size.Reclaimable);
        Assert.Equal(1, result.Size.Entries);
    }

    [Fact]
    public async Task ANamedFileIsOneEntryUnlessTheGuardKeepsIt()
    {
        var old = TempDirectory.Age(_temp.CreateFile(64, "old.lock"), TimeSpan.FromDays(30));
        var recent = _temp.CreateFile(64, "recent.lock");
        var guard = MinimumAge.WithinHours(8, DateTime.UtcNow);

        Assert.Equal(1, (await Walk.MeasureAsync(old, guard)).Size.Entries);
        Assert.Equal(0, (await Walk.MeasureAsync(recent, guard)).Size.Entries);
    }

    [Fact]
    public async Task AnAbsentPathIsNoEntries()
    {
        var result = await Walk.MeasureAsync(Path.Combine(_temp.Path, "never-created"));

        Assert.Equal(0, result.Size.Entries);
    }

    /// <summary>
    /// A folder still holding something cannot be removed, so a file the guard keeps keeps every
    /// folder above it standing. Counting those folders would offer a removal that leaves them all.
    /// A young folder with nothing recent inside it is still counted: the guard is about files, and
    /// a folder's own timestamp moves every time something is added to it.
    /// </summary>
    [Fact]
    public async Task AFileTheGuardKeepsKeepsEveryFolderAboveItOutOfTheCount()
    {
        TempDirectory.Age(_temp.CreateFile(100, "cache", "old.bin"), TimeSpan.FromDays(30));
        TempDirectory.Age(_temp.CreateFile(100, "cache", "live", "stale.bin"), TimeSpan.FromDays(30));
        _temp.CreateFile(100, "cache", "live", "inner", "recent.bin");
        _temp.CreateDirectory("cache", "gone");

        var result = await Walk.MeasureAsync(
            Path.Combine(_temp.Path, "cache"), MinimumAge.WithinHours(8, DateTime.UtcNow));

        // Taken: old.bin, live\stale.bin and gone. Left: recent.bin, and inner, live and cache above it.
        Assert.Equal(3, result.Size.Entries);
        Assert.True(result.WithheldRecent);
    }

    /// <summary>
    /// A link is removed as a link. It is one entry, and nothing on its far side is counted, because
    /// nothing there is removed.
    /// </summary>
    [Fact]
    public async Task ALinkInsideTheFolderIsOneEntryAndIsNeverEntered()
    {
        var outside = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(5000, "elsewhere", "big.bin");
        _temp.CreateDirectory("elsewhere", "deep");

        var cache = _temp.CreateDirectory("cache");
        Directory.CreateSymbolicLink(Path.Combine(cache, "linked"), outside);

        var result = await Walk.MeasureAsync(cache);

        Assert.Equal(2, result.Size.Entries);
        Assert.Equal(0, result.Size.Logical);
    }

    [Fact]
    public void TheIndexCountsTheFolderEveryFolderInItEveryFileAndEveryFolderLink()
    {
        var index = Build(Profile()
            .AddDirectory(8, 7, "cache")
            .AddFile(20, 8, "a.bin", allocated: 1000, logical: 1000)
            .AddDirectory(9, 8, "nested")
            .AddFile(21, 9, "b.bin", allocated: 2000, logical: 2000)
            .AddDirectory(10, 8, "nothing-in-here")
            .AddDirectoryLink(11, 8, "linked")
            .AddFileLink(22, 8, "file-link", logical: 999));

        var size = index.TryMeasure(CachePath)!.Value;

        // cache, nested, nothing-in-here and the folder link, and the two files. The link to a file
        // is not counted on either route: the walk never sees one.
        Assert.Equal(6, size.Entries);
    }

    [Fact]
    public void TheIndexKeepsEveryFolderAboveAFileTheGuardKeepsOutOfTheCount()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var index = Build(Profile()
            .AddDirectory(8, 7, "cache")
            .AddFile(20, 8, "old.bin", 100, 100, created: now.AddDays(-40), lastWritten: now.AddDays(-30))
            .AddDirectory(9, 8, "live")
            .AddDirectory(10, 9, "inner")
            .AddFile(21, 10, "recent.bin", 100, 100, created: now.AddMinutes(-5), lastWritten: now.AddMinutes(-5))
            .AddFile(22, 9, "stale.bin", 100, 100, created: now.AddDays(-40), lastWritten: now.AddDays(-30))
            .AddDirectory(11, 8, "gone", created: now.AddMinutes(-1), lastWritten: now.AddMinutes(-1)));

        var size = index.TryMeasure(CachePath, MinimumAge.WithinHours(8, now), out var withheld)!.Value;

        Assert.Equal(3, size.Entries);
        Assert.True(withheld);
    }

    /// <summary>
    /// The guard keeps files. A folder written a minute ago that holds nothing recent keeps nothing
    /// back, and the walk has always said so. The index used to read the folder's own timestamp as
    /// something withheld, so the same empty folder reported a different state on an elevated run.
    /// </summary>
    [Fact]
    public void AYoungFolderHoldingNothingRecentWithholdsNothingOnTheIndex()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var index = Build(Profile()
            .AddDirectory(8, 7, "cache", created: now.AddMinutes(-1), lastWritten: now.AddMinutes(-1)));

        var size = index.TryMeasure(CachePath, MinimumAge.WithinHours(8, now), out var withheld)!.Value;

        Assert.False(withheld, "an empty folder claimed the guard held something back");
        Assert.Equal(1, size.Entries);
    }

    [Fact]
    public async Task TheTwoRoutesCountOneTreeToTheSameNumber()
    {
        var (path, fixture) = MirroredTree.Realise(_temp, new TreeDirectory(
            "cache",
            new TreeFile("a.tgz", 4096),
            new TreeFile("empty.tmp", 0),
            new TreeDirectory(
                "content-v2",
                new TreeFile("b.tgz", 8000),
                new TreeDirectory("no-entries")),
            new TreeDirectory("sha512", new TreeFile("c.tgz", 1234))));

        var walked = await Walking().MeasureAsync(path);
        var indexed = await Indexing(path, fixture).MeasureAsync(path);

        // The routes have to have been different ones, or this compares a number with itself.
        Assert.Equal(ScanStrategy.ParallelEnumeration, walked.Strategy);
        Assert.Equal(ScanStrategy.MasterFileTable, indexed.Strategy);

        Assert.Equal(8, walked.Size.Entries);
        Assert.Equal(walked.Size.Entries, indexed.Size.Entries);
    }

    [Fact]
    public async Task TheTwoRoutesAgreeOnWhatTheGuardLeavesStanding()
    {
        var now = DateTime.UtcNow;
        var old = now.AddDays(-30);
        var recent = now.AddMinutes(-5);

        var (path, fixture) = MirroredTree.Realise(_temp, new TreeDirectory(
            "cache",
            new TreeFile("old.bin", 100) { Created = old, Modified = old },
            new TreeDirectory(
                "live",
                new TreeDirectory("inner", new TreeFile("recent.bin", 100) { Created = recent, Modified = recent }),
                new TreeFile("stale.bin", 100) { Created = old, Modified = old }),
            new TreeDirectory("gone")));

        var guard = MinimumAge.WithinHours(8, now);

        var walked = await Walking().MeasureAsync(path, guard);
        var indexed = await Indexing(path, fixture).MeasureAsync(path, guard);

        Assert.Equal(ScanStrategy.MasterFileTable, indexed.Strategy);
        Assert.Equal(3, walked.Size.Entries);
        Assert.Equal(walked.Size.Entries, indexed.Size.Entries);
        Assert.Equal(walked.WithheldRecent, indexed.WithheldRecent);
    }

    /// <summary>A remembered size is shown on reopening, and it has to carry its count as well as its bytes.</summary>
    [Fact]
    public async Task ARememberedSizeKeepsItsCount()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var cache = _temp.CreateDirectory("npm-cache");
        _temp.CreateDirectory("npm-cache", "empty");

        DirectoryScanner Reopen() =>
            new(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), new ScanEstimateCache(environment));

        await Reopen().MeasureAsync(cache);

        var progress = new ProgressRecorder<ScanSize>();
        await Reopen().MeasureAsync(cache, MinimumAge.Off, progress);

        Assert.Equal(2, progress.Reports[0].Entries);
    }
}
