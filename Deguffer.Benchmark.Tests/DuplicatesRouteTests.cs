using Deguffer.Core.Duplicates;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The duplicates route times a real search over a tree whose duplicates are known, and reports
/// what each stage kept, asked about and read: cold, every file read again; warm, every read answered
/// by the checksums an untimed search kept. Its checksums are kept in its own folder, never in the
/// app's store.
/// </summary>
public sealed class DuplicatesRouteTests : IDisposable
{
    private const int Block = ContentReader.BlockBytes;

    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string Data => Path.Combine(_tree.Top, "Data");

    /// <summary>
    /// Three small copies, confirmed by reading each whole at the first stage; two large copies, read
    /// at both stages; two large files with the same ends and different middles, parted only by the
    /// full read; two large files whose first blocks differ, parted at the first stage; and one file of
    /// a length no other has, kept by the walk and never identified.
    /// </summary>
    [Fact]
    public async Task AColdRunReportsWhatEachStageKeptAskedAboutAndRead()
    {
        WriteTree();
        var store = ChecksumStoreFolder.Create(_tree.Environment);
        var route = Route(store, warm: false).Timed;

        await route.Before!(CancellationToken.None);
        var tally = await route.Run(CancellationToken.None);
        await route.After!();

        var search = tally.Search!;
        var stages = search.Stages;

        Assert.True(tally.Complete);
        Assert.Equal(2, search.Groups);

        // The ten files and the folders x and y.
        Assert.Equal(12, tally.Items);
        Assert.Equal(10, stages.Kept);
        Assert.Equal(9, stages.Identified);
        Assert.Equal(0, stages.Named);

        Assert.Equal(new ContentReadTally(9, 0, 0, (3 * 5000) + (6 * 2 * Block), stages.Ends.Elapsed), stages.Ends);
        Assert.Equal(new ContentReadTally(4, 0, 0, 4 * 3 * Block, stages.Whole.Elapsed), stages.Whole);
        Assert.Equal(stages.Ends.Bytes + stages.Whole.Bytes, tally.BytesRead);

        // The scratch drive's disks are not described, so it is one lane, read by one reader.
        var lane = Assert.Single(stages.Lanes);
        Assert.Equal(1, lane.Readers);
        Assert.Equal(stages.Ends, lane.Ends);
        Assert.Equal(stages.Whole, lane.Whole);

        Assert.All(Enum.GetValues<DuplicateSearchPart>(), part => Assert.True(stages.Of(part) > TimeSpan.Zero, $"{part} took {stages.Of(part)}"));
    }

    /// <summary>Cold, each run starts with no checksums, so the second run reads every byte the first did.</summary>
    [Fact]
    public async Task EachColdRunReadsEveryFileAgain()
    {
        WriteTree();
        var route = Route(ChecksumStoreFolder.Create(_tree.Environment), warm: false).Timed;

        await route.Before!(CancellationToken.None);
        var first = await route.Run(CancellationToken.None);
        await route.After!();
        await route.Before(CancellationToken.None);
        var second = await route.Run(CancellationToken.None);
        await route.After!();

        Assert.Equal(first.BytesRead, second.BytesRead);
        Assert.Equal(0, second.Search!.Stages.Ends.Remembered + second.Search.Stages.Whole.Remembered);
    }

    /// <summary>
    /// Warm, every read is answered by a checksum the untimed search kept, so no byte is read and the
    /// same groups are confirmed.
    /// </summary>
    [Fact]
    public async Task AWarmRunReadsNoByteAndConfirmsTheSameGroups()
    {
        WriteTree();
        var route = Route(ChecksumStoreFolder.Create(_tree.Environment), warm: true).Timed;

        await route.Before!(CancellationToken.None);
        var tally = await route.Run(CancellationToken.None);
        await route.After!();

        var stages = tally.Search!.Stages;
        Assert.Equal(2, tally.Search.Groups);
        Assert.Equal((9, 9, 0L), (stages.Ends.Files, stages.Ends.Remembered, stages.Ends.Bytes));
        Assert.Equal((4, 4, 0L), (stages.Whole.Files, stages.Whole.Remembered, stages.Whole.Bytes));
        Assert.Equal(0, tally.BytesRead);
    }

    /// <summary>
    /// The checksums go to the route's own folder in the temporary folder and never to the store the
    /// app keeps, which the person's searches read; the folder goes when the route is done with it.
    /// </summary>
    [Fact]
    public async Task ItsChecksumsAreKeptInItsOwnFolderAndNeverInTheApps()
    {
        WriteTree();
        var store = ChecksumStoreFolder.Create(_tree.Environment);
        var route = Route(store, warm: true).Timed;

        await route.Before!(CancellationToken.None);
        await route.Run(CancellationToken.None);
        await route.After!();

        Assert.StartsWith(_tree.Environment.TempPath + Path.DirectorySeparatorChar, store.Root, StringComparison.Ordinal);
        Assert.True(File.Exists(store.Store));
        Assert.False(File.Exists(ChecksumCache.FileOf(_tree.Environment)));

        Assert.True(await store.RemoveAsync());
        Assert.False(Directory.Exists(store.Root));
    }

    private DuplicatesRoute Route(ChecksumStoreFolder store, bool warm) => new(
        _tree.Finder(),
        new VolumeMediaCache(new FakeStorageQueries()),
        _tree.Policy(),
        DuplicatesRoute.Search([Data], ChecksumAlgorithm.XxHash128),
        warm,
        store.Store);

    private void WriteTree()
    {
        var small = Random(5000, 1);
        _tree.File(small, "Data", "a1.bin");
        _tree.File(small, "Data", "x", "a2.bin");
        _tree.File(small, "Data", "y", "a3.bin");

        var large = Random(3 * Block, 2);
        _tree.File(large, "Data", "b1.bin");
        _tree.File(large, "Data", "x", "b2.bin");

        // The same first and last blocks, and a different middle.
        var middle = Random(3 * Block, 3);
        _tree.File(middle, "Data", "c1.bin");
        middle[Block + 1] ^= 0xFF;
        _tree.File(middle, "Data", "x", "c2.bin");

        // A different first block, and the same rest.
        var start = Random(3 * Block, 4);
        _tree.File(start, "Data", "d1.bin");
        start[0] ^= 0xFF;
        _tree.File(start, "Data", "x", "d2.bin");

        _tree.File(Random(7000, 5), "Data", "lone.bin");
    }

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);

        return bytes;
    }
}
