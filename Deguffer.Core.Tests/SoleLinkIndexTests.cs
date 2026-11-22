using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sole-link figure of a store (pnpm's, Bun's, the driver store's) from the volume index, where the
/// rows beside it are building one, and from the walk everywhere else (#1066).
///
/// <para>Only the estimate depends on this: each store's own command decides what goes (§5.1). The
/// figure still has to be the same whichever route answers, or one store reads two ways depending on
/// how the app was launched.</para>
/// </summary>
public class SoleLinkIndexTests
{
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Store = 8;
    private const uint Consumer = 9;
    private const uint Outside = 9_000;

    private static readonly string[] StorePath = ["Users", "testuser", "store"];

    /// <summary>
    /// A store holding files of one, two and three names, one with an 8.3 alias, and a folder below
    /// it, with the other names in a consumer beside the store and in the store itself.
    /// </summary>
    private static TreeDirectory Pnpm(params TreeEntry[] more) => new(
        "pnpm",
        new TreeDirectory(
            "store",
            [
                new TreeFile("one.bin", 4096),
                new TreeFile("a-long-file-name.bin", 8192, Alias: "A-LONG~1.BIN"),
                new TreeFile("two.bin", 12288),
                new TreeFile("three.bin", 16384),
                new TreeLink("three-again.bin", "store", "three.bin"),
                new TreeDirectory("inner", new TreeFile("deep.bin", 20480)),
                .. more,
            ]),
        new TreeDirectory(
            "consumer",
            new TreeLink("two.bin", "store", "two.bin"),
            new TreeLink("three.bin", "store", "three.bin")));

    /// <summary>The files of <see cref="Pnpm"/>'s store with one name, the alias not being one.</summary>
    private const long SoleLinked = 4096 + 8192 + 20480;

    private static DirectoryScanner Scanner(IMftSourceFactory sources, ScanTuner tuning) => new(sources, tuning: tuning);

    /// <summary>
    /// The index's figure equals the walk's over one tree: only the files with a single name count, a
    /// name in the store and one beside it alike, and an 8.3 alias is not a second name.
    /// </summary>
    [Fact]
    public async Task TheIndexCountsTheFilesWithOneNameAsTheWalkDoes()
    {
        using var temp = new TempDirectory();

        var (path, fixture) = MirroredTree.Realise(temp, Pnpm());
        var store = Path.Combine(path, "store");
        var scanner = Scanner(FakeMftSourceFactory.Serving(path[0], fixture), RouteTuners.Table);

        using var pass = scanner.Hold();

        // The rows beside the store build the index, as an elevated pass's do.
        await scanner.MeasureAsync(path);

        var walked = await HardLinkAwareScanner.Default.MeasureAsync(store);
        var indexed = await scanner.HardLinks.MeasureAsync(store);

        // Two routes, or this compares a number with itself.
        Assert.Equal(ScanStrategy.ParallelEnumeration, walked.Strategy);
        Assert.Equal(ScanStrategy.MasterFileTable, indexed.Strategy);

        Assert.Equal(SoleLinked, walked.Size.Logical);
        Assert.Equal(walked.Size.Logical, indexed.Size.Logical);
        Assert.Equal(walked.Size.Reclaimable, indexed.Size.Reclaimable);

        // A prediction on both routes, counting nothing a removal of Deguffer's takes.
        Assert.True(walked.Size.IsApproximate);
        Assert.True(indexed.Size.IsApproximate);
        Assert.Equal(0, walked.Size.Entries);
        Assert.Equal(0, indexed.Size.Entries);
    }

    /// <summary>
    /// No row has started a build of the volume, so the store is walked, and no build is started for
    /// it: a build reads the whole volume, which one store's walk does not.
    /// </summary>
    [Fact]
    public async Task AStoreWhoseVolumeHasNoBuildIsWalkedAndStartsNone()
    {
        using var temp = new TempDirectory();

        var (path, fixture) = MirroredTree.Realise(temp, Pnpm());
        var sources = FakeMftSourceFactory.Serving(path[0], fixture);
        var scanner = Scanner(sources, RouteTuners.Table);

        var walked = await scanner.HardLinks.MeasureAsync(Path.Combine(path, "store"));

        Assert.Equal(0, sources.OpenCount);
        Assert.Equal(ScanStrategy.ParallelEnumeration, walked.Strategy);
        Assert.Equal(FallbackReason.None, walked.Fallback);
        Assert.Equal(SoleLinked, walked.Size.Logical);
    }

    /// <summary>
    /// A file the table holds no size for sends the store to the walk, which reads it, while a folder
    /// below it whose sizes the table does know is still answered from the index.
    /// </summary>
    [Fact]
    public async Task AStoreHoldingASizeTheTableDoesNotKnowIsWalked()
    {
        using var temp = new TempDirectory();

        var (path, fixture) = MirroredTree.Realise(temp, Pnpm(new TreeFile("split.bin", 4096, SizeUnknownToTable: true)));
        var store = Path.Combine(path, "store");
        var scanner = Scanner(FakeMftSourceFactory.Serving(path[0], fixture), RouteTuners.Table);

        using var pass = scanner.Hold();

        await scanner.MeasureAsync(path);

        var measured = await scanner.HardLinks.MeasureAsync(store);
        var inner = await scanner.HardLinks.MeasureAsync(Path.Combine(store, "inner"));

        Assert.Equal(ScanStrategy.ParallelEnumeration, measured.Strategy);
        Assert.Equal(FallbackReason.None, measured.Fallback);
        Assert.Equal(SoleLinked + 4096, measured.Size.Logical);

        Assert.Equal(ScanStrategy.MasterFileTable, inner.Strategy);
        Assert.Equal(20480, inner.Size.Logical);
    }

    /// <summary>
    /// A build still reading the table is raced, as every other row races it, so the store is not
    /// held up behind a pass over the whole volume, and the walk that answers offers nothing.
    /// </summary>
    [Fact]
    public async Task ABuildStillReadingIsRacedByTheWalk()
    {
        using var temp = new TempDirectory();

        var (path, fixture) = MirroredTree.Realise(temp, Pnpm());
        var sources = new HeldMftSourceFactory(path[0], fixture);
        var scanner = Scanner(sources, RouteTuners.Auto);

        using var pass = scanner.Hold();

        try
        {
            // The row beside the store starts the build, and its walk answers while the table is held.
            await scanner.MeasureAsync(path).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

            var walked = await scanner.HardLinks.MeasureAsync(Path.Combine(path, "store")).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, sources.OpenCount);
            Assert.Equal(ScanStrategy.ParallelEnumeration, walked.Strategy);
            Assert.Equal(FallbackReason.None, walked.Fallback);
            Assert.Equal(SoleLinked, walked.Size.Logical);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A file's other name in a directory the table does not hold is dropped from the tree, and is
    /// still a link Windows counts, so the file is not sole-linked. Counting the names the tree placed
    /// would report its bytes as reclaimable.
    /// </summary>
    [Fact]
    public void ANameInADirectoryOutsideTheTableStillCounts()
    {
        using var source = new MftFixture()
            .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
            .AddDirectory(Profile, Users, "testuser")
            .AddDirectory(Store, Profile, "store")
            .AddDirectory(Consumer, Profile, "consumer")
            .AddFile(20, Store, "one.bin", allocated: 4096, logical: 4096)
            .AddHardLinkedFile(21, [(Store, "away.bin"), (Outside, "away.bin")], allocated: 8192, logical: 8192)
            .AddHardLinkedFile(22, [(Store, "near.bin"), (Consumer, "near.bin")], allocated: 16384, logical: 16384)
            .AddHardLinkedFile(23, [(Store, "aliased-name.bin")], allocated: 2048, logical: 2048, alias: "ALIASE~1.BIN")
            .Build();

        Assert.True(MftVolumeIndexBuilder.TryBuild(source, TableTuning.Default, out var index));

        var sole = index.TryMeasure(StorePath, [MinimumAge.Off], keepRoot: false, MftVolumeIndex.NothingLeftOut, LinkCounting.SoleName)!;
        var each = index.TryMeasure(StorePath, [MinimumAge.Off], keepRoot: false, MftVolumeIndex.NothingLeftOut, LinkCounting.EachName)!;

        Assert.Equal(ScanSize.Approximate(4096 + 2048, 4096 + 2048), sole.Readings[0].Size);
        Assert.Equal(4096 + 8192 + 16384 + 2048, each.Readings[0].Size.Logical);
    }
}
