using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// One reading of a tree answering for both the whole of it and its aged part, as File History's
/// command step needs. Every route has to give exactly the two answers two separate measurements
/// would, entry counts included: the folders an age keeps standing are not the folders the whole
/// tree's removal keeps, and a tally that shared them would count one reading's folders in the other.
///
/// <para>The tree holds a folder of old files only, one of new files only, and one of both with an
/// old-only folder inside it, so each kind of folder is decided differently by the two readings. The
/// mixed folder also holds a mail store, which every reading leaves standing, listed after a new
/// file: the store's folder is then already marked for the aged reading alone, and the mark for both
/// still has to reach the root.</para>
/// </summary>
public sealed class AgedScanTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static readonly MinimumAge Retention = MinimumAge.Within(TimeSpan.FromDays(30), Now);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>The mixed tree on disk, with its root's path.</summary>
    private string CreateTree()
    {
        TempDirectory.Age(_temp.CreateFile(1000, "history", "old", "a.bin"), TimeSpan.FromDays(800));
        _temp.CreateFile(2000, "history", "new", "b.bin");
        TempDirectory.Age(_temp.CreateFile(4000, "history", "mixed", "c.bin"), TimeSpan.FromDays(800));
        _temp.CreateFile(8000, "history", "mixed", "a-new.bin");
        _temp.CreateFile(512, "history", "mixed", "archive.pst");
        TempDirectory.Age(_temp.CreateFile(16000, "history", "mixed", "deep", "e.bin"), TimeSpan.FromDays(800));

        return Path.Combine(_temp.Path, "history");
    }

    private static void AssertSameReading(ScanResult expected, ScanResult actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.WithheldRecent, actual.WithheldRecent);
        Assert.Equal(expected.Strategy, actual.Strategy);
        Assert.Equal(expected.Fallback, actual.Fallback);
        Assert.Equal(expected.Root, actual.Root);
        Assert.Equal(expected.MailStores, actual.MailStores);
    }

    private static async Task AssertBothReadingsMatchAsync(IDirectoryScanner scanner, string path)
    {
        var scanned = await scanner.MeasureAgedAsync(path, Retention);

        AssertSameReading(await scanner.MeasureAsync(path), scanned.Whole);
        AssertSameReading(await scanner.MeasureAsync(path, Retention), scanned.Aged);
    }

    /// <summary>
    /// The walk. Whole: five files, and the three folders the store does not keep standing. Aged: the
    /// three old files, and the two folders holding nothing new or kept.
    /// </summary>
    [Fact]
    public async Task TheWalkTalliesBothReadingsAsTwoMeasurementsWould()
    {
        var tree = CreateTree();

        var scanned = await ParallelEnumerationScanner.Default.MeasureAgedAsync(tree, Retention);

        Assert.Equal(new ScanSize(31000, 31000, Entries: 8), scanned.Whole.Size);
        Assert.False(scanned.Whole.WithheldRecent);
        Assert.Equal(new ScanSize(21000, 21000, Entries: 5), scanned.Aged.Size);
        Assert.True(scanned.Aged.WithheldRecent);

        await AssertBothReadingsMatchAsync(ParallelEnumerationScanner.Default, tree);
    }

    /// <summary>
    /// Unelevated, the route-choosing scanner hands both readings to one walk, and both carry the
    /// reason the walk was taken.
    /// </summary>
    [Fact]
    public async Task TheFallbackWalkCarriesItsReasonOnBothReadings()
    {
        var tree = CreateTree();
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

        var scanned = await scanner.MeasureAgedAsync(tree, Retention);

        Assert.Equal(FallbackReason.NotElevated, scanned.Whole.Fallback);
        Assert.Equal(FallbackReason.NotElevated, scanned.Aged.Fallback);

        await AssertBothReadingsMatchAsync(scanner, tree);
    }

    /// <summary>The same tree in a volume's table, read through one built index.</summary>
    [Fact]
    public async Task TheTableAnswersBothReadingsAsTwoMeasurementsWould()
    {
        const uint users = 6, profile = 7, history = 8, old = 9, recent = 10, mixed = 11, deep = 12;

        var then = Now - TimeSpan.FromDays(800);
        var lately = Now - TimeSpan.FromMinutes(5);

        var volume = new MftFixture()
            .AddDirectory(users, MftRecord.RootRecordNumber, "Users")
            .AddDirectory(profile, users, "testuser")
            .AddDirectory(history, profile, "history")
            .AddDirectory(old, history, "old")
            .AddDirectory(recent, history, "new")
            .AddDirectory(mixed, history, "mixed")
            .AddDirectory(deep, mixed, "deep")
            .AddFile(20, old, "a.bin", allocated: 4096, logical: 1000, created: then, lastWritten: then)
            .AddFile(21, recent, "b.bin", allocated: 4096, logical: 2000, created: lately, lastWritten: lately)
            .AddFile(22, mixed, "c.bin", allocated: 4096, logical: 4000, created: then, lastWritten: then)
            .AddFile(23, mixed, "a-new.bin", allocated: 8192, logical: 8000, created: lately, lastWritten: lately)
            .AddFile(25, mixed, "archive.pst", allocated: 4096, logical: 512, created: then, lastWritten: then)
            .AddFile(24, deep, "e.bin", allocated: 16384, logical: 16000, created: then, lastWritten: then);

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', volume), tuning: RouteTuners.Table);
        const string path = @"C:\Users\testuser\history";

        var scanned = await scanner.MeasureAgedAsync(path, Retention);

        Assert.Equal(ScanStrategy.MasterFileTable, scanned.Whole.Strategy);
        Assert.Equal(31000, scanned.Whole.Size.Logical);
        Assert.Equal(21000, scanned.Aged.Size.Logical);
        Assert.NotEqual(scanned.Whole.Size.Entries, scanned.Aged.Size.Entries);

        await AssertBothReadingsMatchAsync(scanner, path);
    }

    /// <summary>The link-aware walk, whose figures are sole-linked bytes rather than lengths.</summary>
    [Fact]
    public async Task TheLinkAwareWalkTalliesBothReadingsAsTwoMeasurementsWould()
    {
        var tree = CreateTree();

        var scanned = await HardLinkAwareScanner.Default.MeasureAgedAsync(tree, Retention);

        Assert.Equal(31000, scanned.Whole.Size.Logical);
        Assert.Equal(21000, scanned.Aged.Size.Logical);
        Assert.True(scanned.Aged.WithheldRecent);

        await AssertBothReadingsMatchAsync(HardLinkAwareScanner.Default, tree);
    }

    /// <summary>
    /// Reading a file's link count is the link-aware walk's whole per-file cost, so a file both tallies
    /// count is read once, not once for each. The mail store is never read, as before.
    /// </summary>
    [Fact]
    public async Task TheLinkAwareWalkReadsEachFileOnceForBothTallies()
    {
        var tree = CreateTree();
        var reads = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scanner = new HardLinkAwareScanner(ScanTuner.Shipped, Counting(reads));

        await scanner.MeasureAgedAsync(tree, Retention);

        Assert.Equal(5, reads.Count);
        Assert.All(reads, read => Assert.Equal(1, read.Value));
        Assert.DoesNotContain(reads.Keys, path => path.EndsWith(".pst", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A single file is measured for both readings, and the guard decides each of them.</summary>
    [Theory]
    [InlineData(800)]
    [InlineData(0)]
    public async Task ASingleFileIsMeasuredForBothReadings(int days)
    {
        var file = TempDirectory.Age(_temp.CreateFile(4096, "dumps", "MEMORY.DMP"), TimeSpan.FromDays(days));

        await AssertBothReadingsMatchAsync(ParallelEnumerationScanner.Default, file);
        await AssertBothReadingsMatchAsync(HardLinkAwareScanner.Default, file);
    }

    /// <summary>A single file the link-aware scanner measures is read once for both readings.</summary>
    [Fact]
    public async Task TheLinkAwareScannerReadsASingleFileOnceForBothReadings()
    {
        var file = TempDirectory.Age(_temp.CreateFile(4096, "dumps", "MEMORY.DMP"), TimeSpan.FromDays(800));
        var reads = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var scanned = await new HardLinkAwareScanner(ScanTuner.Shipped, Counting(reads)).MeasureAgedAsync(file, Retention);

        Assert.Equal(4096, scanned.Aged.Size.Logical);
        Assert.Equal(1, Assert.Single(reads).Value);
    }

    /// <summary>The real reads, each one counted against the path it read.</summary>
    private static FileInformation Counting(ConcurrentDictionary<string, int> reads) => new(
        FileInformation.Open,
        FileInformation.ReadIdentity,
        readByName: (string path, out FileInformation.FileStandardInfo info) =>
        {
            reads.AddOrUpdate(path, 1, (_, count) => count + 1);
            return StatByName.Read(path, out info);
        });
}
