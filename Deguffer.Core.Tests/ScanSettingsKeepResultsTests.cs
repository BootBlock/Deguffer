using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every scan setting changes how fast a scan is and never what it finds. Each test here scans the
/// same tree, or reads the same table, at the smallest and the largest value of every setting, and
/// asserts the results are identical, through the settings rather than around them.
///
/// <para>The tree holds what the rules are about: a folder Windows refuses, which is still reported
/// and still stays, and a link, which is never followed. The tables hold records whose attributes
/// continue in extension records, which are read after the first pass, so a read size that cut a
/// batch in the wrong place would lose them.</para>
/// </summary>
public sealed class ScanSettingsKeepResultsTests : IDisposable
{
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Cache = 8;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static MediaScanPreferences Smallest { get; } = new(
        WalkTuning.MinimumThreads, WalkTuning.MinimumListingBuffer / 1024, TableTuning.MinimumReadBytes / 1024);

    private static MediaScanPreferences Largest { get; } = new(
        WalkTuning.MaximumThreads, WalkTuning.MaximumListingBuffer / 1024, TableTuning.MaximumReadBytes / 1024);

    [Fact]
    public async Task TheWalkMeasuresTheSameTreeAtTheSmallestAndLargestValues()
    {
        var root = Tree();
        var outside = _temp.CreateDirectory("outside");
        _temp.CreateFile(1 << 20, "outside", "not-ours.bin");
        Junction.ToDirectory(Path.Combine(root, "link"), outside);

        var refused = _temp.CreateDirectory("cache", "branch-1", "refused");
        _temp.CreateFile(64, "cache", "branch-1", "refused", "unreachable.bin");
        using var denied = new DeniedDirectory(refused);

        // NTFS updates the times a folder's parent keeps for it lazily, so the first walk of a new
        // tree can read times the second reads newer. One walk first leaves both compared below
        // reading the same disk.
        await Walking(MediaScanPreferences.Auto).MeasureAsync(root);

        var smallest = await Walking(Smallest).MeasureAsync(root);
        var largest = await Walking(Largest).MeasureAsync(root);

        Assert.Equal(ScanStrategy.ParallelEnumeration, smallest.Strategy);
        Assert.Equal(Expected, smallest.Size.Logical);
        AssertSame(smallest, largest);
    }

    [Fact]
    public async Task TheTableMeasuresTheSameTreeAtTheSmallestAndLargestValues()
    {
        var smallest = await Indexing(Smallest).MeasureAsync(@"C:\Users\testuser");
        var largest = await Indexing(Largest).MeasureAsync(@"C:\Users\testuser");

        Assert.Equal(ScanStrategy.MasterFileTable, smallest.Strategy);
        Assert.Equal(TableTotal, smallest.Size.Allocated);
        AssertSame(smallest, largest);
    }

    /// <summary>
    /// A short read is never skipped past at any size. A read size that stepped over the region it
    /// could not read would total what it did get and say nothing.
    /// </summary>
    [Fact]
    public async Task ATableThatCannotBeReadInFullIsRefusedAtTheSmallestAndLargestValues()
    {
        var smallest = await Indexing(Smallest, unreadableFrom: 40).MeasureAsync(@"C:\Users\testuser");
        var largest = await Indexing(Largest, unreadableFrom: 40).MeasureAsync(@"C:\Users\testuser");

        Assert.Equal(FallbackReason.MasterFileTableIncomplete, smallest.Fallback);
        Assert.Equal(FallbackReason.MasterFileTableIncomplete, largest.Fallback);
    }

    [Fact]
    public async Task ExploreReadsTheSameTableAtTheSmallestAndLargestValues()
    {
        var smallest = await Exploring(Smallest).ScanAsync(@"C:\");
        var largest = await Exploring(Largest).ScanAsync(@"C:\");

        Assert.Equal(ScanStrategy.MasterFileTable, smallest.Strategy);
        Assert.Equal(Describe(smallest.Tree), Describe(largest.Tree));
        Assert.Contains(Describe(smallest.Tree), line => line.StartsWith(@"C:\Users\testuser\.npm-cache\listed.tgz|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExploreWalksTheSameTreeAtTheSmallestAndLargestValues()
    {
        var root = Tree();

        await WalkingExplore(MediaScanPreferences.Auto).ScanAsync(root);

        var smallest = await WalkingExplore(Smallest).ScanAsync(root);
        var largest = await WalkingExplore(Largest).ScanAsync(root);

        Assert.Equal(Describe(smallest.Tree), Describe(largest.Tree));
    }

    /// <summary>
    /// The volume source cuts every read to whole sectors and whole records, so the size is held to
    /// each layout Windows produces: records smaller than sectors, records the size of a sector, and
    /// a table in two extents.
    /// </summary>
    [Theory]
    [InlineData(512, 4096, 1024, null)]
    [InlineData(4096, 4096, 1024, null)]
    [InlineData(4096, 4096, 1024, 2L)]
    [InlineData(4096, 4096, 4096, 7L)]
    public void AVolumeIsReadTheSameAtTheSmallestAndLargestReadSize(
        int bytesPerSector, int bytesPerCluster, int bytesPerRecord, long? gapAfterCluster)
    {
        var smallest = ReadVolume(bytesPerSector, bytesPerCluster, bytesPerRecord, gapAfterCluster, TableTuning.MinimumReadBytes);
        var largest = ReadVolume(bytesPerSector, bytesPerCluster, bytesPerRecord, gapAfterCluster, TableTuning.MaximumReadBytes);

        Assert.Equal(TableTotal, smallest);
        Assert.Equal(smallest, largest);
    }

    /// <summary>The bytes the walked tree holds, every file reached and none behind the link or the refusal.</summary>
    private const long Expected = (1500 * 3) + (6 * (10 + 20 + 30 + 40));

    private const long TableTotal = (40 * 4096) + 8192 + 8192;

    private string Tree()
    {
        var root = _temp.CreateDirectory("cache");

        for (var i = 0; i < 1500; i++)
        {
            _temp.CreateFile(3, "cache", "wide", $"entry-with-a-longer-name-{i:D4}.bin");
        }

        for (var branch = 0; branch < 6; branch++)
        {
            for (var depth = 1; depth <= 4; depth++)
            {
                _temp.CreateFile(
                    depth * 10,
                    [.. new[] { "cache", $"branch-{branch}" }.Concat(Enumerable.Range(1, depth).Select(d => $"level-{d}")), $"file-{depth}.bin"]);
            }
        }

        return root;
    }

    /// <summary>
    /// A home folder of forty files, then two whose attributes continue in extension records,
    /// numbered past the rest so they are held back and handed on after the first pass.
    /// </summary>
    private static MftFixture Table(int bytesPerRecord = MftRecordBytes.BytesPerRecord)
    {
        var fixture = new MftFixture(bytesPerRecord)
            .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
            .AddDirectory(Profile, Users, "testuser")
            .AddDirectory(Cache, Profile, ".npm-cache");

        for (uint i = 0; i < 40; i++)
        {
            fixture.AddFile(20 + i, Cache, $"file-{i:D2}.bin", allocated: 4096, logical: 4000);
        }

        return fixture
            .AddFileWithDataInAnExtensionRecord(60, Cache, "extended.tgz", allocated: 8192, logical: 8000, extension: 61)
            .AddFileWithItsNameInAnExtensionRecord(62, Cache, "listed.tgz", allocated: 8192, logical: 8000, extension: 63);
    }

    private static long ReadVolume(
        int bytesPerSector, int bytesPerCluster, int bytesPerRecord, long? gapAfterCluster, int readBytes)
    {
        using var source = VolumeMftSource.TryOpen(
            Table(bytesPerRecord).BuildVolume(bytesPerSector, bytesPerCluster, gapAfterCluster), out _);

        Assert.NotNull(source);
        Assert.True(MftVolumeIndexBuilder.TryBuild(source, new TableTuning(readBytes), out var index));

        return index.TryMeasure(["Users", "testuser"])!.Value.Allocated;
    }

    /// <summary>A tuner that finds every drive of unknown kind, and the given values for that kind.</summary>
    private static ScanTuner Tuner(MediaScanPreferences values) =>
        new(
            new FakePreferences(AppPreferences.Default with
            {
                Scanning = ScanPreferences.Default.With(StorageMedia.Unknown, values),
            }),
            new VolumeMediaCache(new FakeStorageQueries()),
            new FakeVolumeInventory());

    private static DirectoryScanner Walking(MediaScanPreferences values) =>
        new(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), tuning: Tuner(values));

    private static DirectoryScanner Indexing(MediaScanPreferences values, long? unreadableFrom = null)
    {
        var table = Table();

        if (unreadableFrom is { } from)
        {
            table.UnreadableFrom(from);
        }

        return new DirectoryScanner(FakeMftSourceFactory.Serving('C', table), tuning: Tuner(values));
    }

    private static ExploreScanner Exploring(MediaScanPreferences values) =>
        new(FakeMftSourceFactory.Serving('C', Table()), tuning: Tuner(values));

    private static ExploreScanner WalkingExplore(MediaScanPreferences values) =>
        new(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), tuning: Tuner(values));

    private static void AssertSame(ScanResult expected, ScanResult actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.Strategy, actual.Strategy);
        Assert.Equal(expected.Fallback, actual.Fallback);
        Assert.Equal(expected.Root, actual.Root);
        Assert.Equal(expected.WithheldRecent, actual.WithheldRecent);
        Assert.Equal(expected.MailStores, actual.MailStores);
    }

    /// <summary>Every node as its path, size and kind, in path order.</summary>
    private static List<string> Describe(ExploreTree tree)
    {
        var lines = new List<string>();
        var pending = new Stack<int>([tree.RootNode]);

        while (pending.TryPop(out var node))
        {
            lines.Add(string.Join(
                '|',
                tree.PathOf(node),
                tree.SizeOf(node),
                tree.IsDirectory(node),
                tree.IsLink(node),
                tree.HasUnknownSizeBelow(node)));

            foreach (var child in tree.ChildrenOf(node))
            {
                pending.Push(child);
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }
}
