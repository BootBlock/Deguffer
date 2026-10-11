using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The numbering contract <see cref="ExploreTreeBuilder.AddChildren"/> offers, which is the only
/// thing the walk has to relate a child directory to the node it just recorded.
///
/// <para>It is worth its own tests because it is a contract about <em>numbers</em> rather than about
/// content, so breaking it produces a tree that is entirely well formed and describes a different
/// disk: every node present, every size right, and files hanging off the wrong directories. Nothing
/// downstream can detect that, and the walk records from several threads at once, so an off-by-one
/// would appear only under load.</para>
/// </summary>
public class ExploreTreeBuilderTests
{
    private const string Root = @"C:\Users\testuser";

    /// <summary>
    /// The first node number back, and the rest following it in the order they were given. The walk
    /// descends by adding an entry's index to this number, so an ordering or an offset that is out
    /// by one attaches a whole subtree to the wrong sibling.
    /// </summary>
    [Fact]
    public void NumbersOneDirectorysChildrenConsecutivelyFromTheNumberItReturns()
    {
        var builder = new ExploreTreeBuilder(Root);

        var first = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("cache", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("a.tgz", IsDirectory: false, IsLink: false, Size: 4096),
            new ExploreChild("b.tgz", IsDirectory: false, IsLink: false, Size: 8192),
        ]);

        var second = builder.AddChildren(first, [
            new ExploreChild("content-v2", IsDirectory: true, IsLink: false, Size: 0),
        ]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(ExploreTreeBuilder.RootNode + 1, first);
        Assert.Equal(first + 3, second);

        Assert.Equal("cache", tree.NameOf(first));
        Assert.Equal("a.tgz", tree.NameOf(first + 1));
        Assert.Equal("b.tgz", tree.NameOf(first + 2));
        Assert.Equal("content-v2", tree.NameOf(second));

        Assert.Equal(first, tree.ParentOf(second));
        Assert.Equal(4096, tree.SizeOf(first + 1));
        Assert.True(tree.IsDirectory(first));
        Assert.False(tree.IsDirectory(first + 1));
    }

    /// <summary>
    /// The same contract while several threads record at once, which is how the walk uses it: a
    /// level's directories are read in parallel and each one hands its whole listing over as it
    /// finishes.
    ///
    /// <para>Every batch is checked back by name, so this fails on any interleaving that hands two
    /// callers overlapping ranges as well as on one that loses entries outright. Both are silent:
    /// the tree still builds, and the only evidence is a file under a directory it was never in.
    /// </para>
    /// </summary>
    [Fact]
    public void HandsOutDisjointRangesWhenSeveralThreadsRecordAtOnce()
    {
        const int batches = 256;
        const int perBatch = 8;

        var builder = new ExploreTreeBuilder(Root);
        var firsts = new int[batches];

        Parallel.For(0, batches, batch => firsts[batch] = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, perBatch).Select(entry =>
                new ExploreChild($"{batch}-{entry}.bin", IsDirectory: false, IsLink: false, Size: 1))]));

        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal((batches * perBatch) + 1, tree.NodeCount);
        Assert.Equal(batches * perBatch, tree.TotalBytes);

        for (var batch = 0; batch < batches; batch++)
        {
            for (var entry = 0; entry < perBatch; entry++)
            {
                Assert.Equal($"{batch}-{entry}.bin", tree.NameOf(firsts[batch] + entry));
            }
        }
    }

    /// <summary>
    /// A snapshot taken mid-scan is a tree in its own right, not a view over one still being
    /// written: §5.5 has the walk publish these while it runs, and a caller that kept drawing from
    /// the arrays underneath would race every subsequent directory the walk records.
    /// </summary>
    [Fact]
    public void BuildsASnapshotThatLaterAdditionsDoNotChange()
    {
        var builder = new ExploreTreeBuilder(Root);

        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("a.tgz", IsDirectory: false, IsLink: false, Size: 4096),
        ]);

        var snapshot = builder.Build(ExploreChildOrder.BySize);

        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("b.tgz", IsDirectory: false, IsLink: false, Size: 8192),
        ]);

        Assert.Equal(4096, snapshot.TotalBytes);
        Assert.Equal(2, snapshot.NodeCount);
        Assert.Equal(12_288, builder.Build(ExploreChildOrder.BySize).TotalBytes);
    }

    /// <summary>
    /// A folder's length is its files' lengths added up, beside its space on disk, and it holds every
    /// kind of storage found under it. A child that names no length is one whose length is its size,
    /// which is every file a listing alone describes.
    /// </summary>
    [Fact]
    public void RollsLengthsAndStorageUpBesideTheSpaceOnDisk()
    {
        var builder = new ExploreTreeBuilder(Root);
        var folder = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("OneDrive", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("plain.tgz", IsDirectory: false, IsLink: false, Size: 4096),
        ]);
        builder.AddChildren(folder, [
            new ExploreChild("film.mkv", IsDirectory: false, IsLink: false, Size: 0, Length: 5_000_000, Storage: FileStorage.CloudOnly),
            new ExploreChild("log.txt", IsDirectory: false, IsLink: false, Size: 1024, Length: 8192, Storage: FileStorage.Compressed),
        ]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(1024, tree.SizeOf(folder));
        Assert.Equal(5_008_192, tree.LengthOf(folder));
        Assert.Equal(FileStorage.CloudOnly | FileStorage.Compressed, tree.StorageOf(folder));
        Assert.Equal(4096, tree.LengthOf(folder + 1));
        Assert.Equal(5_012_288, tree.TotalLength);
        Assert.Equal(5120, tree.TotalBytes);
    }

    /// <summary>A file the file system would not measure makes every total above it a lower bound.</summary>
    [Fact]
    public void CarriesAFileItCouldNotMeasureAsALowerBound()
    {
        var builder = new ExploreTreeBuilder(Root);
        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("gone.mkv", IsDirectory: false, IsLink: false, Size: 0, Length: 5_000_000, SizeUnknown: true),
        ]);

        Assert.True(builder.Build(ExploreChildOrder.BySize).HasUnknownSizes);
    }

    /// <summary>
    /// A tree is assembled from a capture without the lock every walk worker needs, so a worker
    /// records its next folder while a snapshot is still being assembled rather than waiting for it.
    /// The lock was once held for the whole of the assembling, which on a large walk stopped every
    /// worker for as long as each snapshot took.
    ///
    /// <para>The worker records from another thread, so a capture that still held the lock would
    /// keep it waiting until the wait gives up.</para>
    /// </summary>
    [Fact]
    public async Task RecordsMoreWhileACaptureIsStillToBeAssembled()
    {
        var builder = new ExploreTreeBuilder(Root);
        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("a.tgz", IsDirectory: false, IsLink: false, Size: 4096),
        ]);

        var capture = builder.Capture();

        var recording = Task.Run(() => builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("b.tgz", IsDirectory: false, IsLink: false, Size: 8192),
        ]));

        Assert.Same(recording, await Task.WhenAny(recording, Task.Delay(TimeSpan.FromSeconds(30))));

        var tree = capture.Build(ExploreChildOrder.ByName);

        Assert.Equal(2, tree.NodeCount);
        Assert.Equal(4096, tree.TotalBytes);
        Assert.Equal(12_288, builder.Build(ExploreChildOrder.BySize).TotalBytes);
    }

    /// <summary>
    /// A capture assembled while the walk keeps recording, and growing past the chunks it was
    /// taken over, holds exactly what was recorded when it was taken, each node as it was recorded.
    /// A reader outside the lock is only safe because nothing it reads is moved or written again.
    /// </summary>
    [Fact]
    public async Task AssemblesWhatWasCapturedWhileTheWalkKeepsRecording()
    {
        const int batches = 200;
        const int perBatch = 500;

        var builder = new ExploreTreeBuilder(Root);
        AddFiles(builder, 0, batches);

        var capture = builder.Capture();
        var recording = Task.Run(() => AddFiles(builder, batches, batches * 2));

        var tree = capture.Build(ExploreChildOrder.ByName);
        await recording;

        Assert.Equal((batches * perBatch) + 1, tree.NodeCount);
        Assert.Equal(batches * perBatch, tree.TotalBytes);

        for (var node = 1; node < tree.NodeCount; node++)
        {
            var batch = (node - 1) / perBatch;
            Assert.Equal($"{batch}-{(node - 1) % perBatch:D3}.bin", tree.NameOf(node));
            Assert.Equal(ExploreTreeBuilder.RootNode, tree.ParentOf(node));
        }

        Assert.Equal((2 * batches * perBatch) + 1, builder.Build(ExploreChildOrder.ByName).NodeCount);

        static void AddFiles(ExploreTreeBuilder builder, int from, int to)
        {
            for (var batch = from; batch < to; batch++)
            {
                builder.AddChildren(ExploreTreeBuilder.RootNode, [.. Enumerable.Range(0, perBatch).Select(entry =>
                    new ExploreChild($"{batch}-{entry:D3}.bin", IsDirectory: false, IsLink: false, Size: 1))]);
            }
        }
    }

    /// <summary>
    /// A snapshot puts each folder's children in name order from the order recorded with them,
    /// which has to be the order <see cref="ExploreChildOrder.ByName"/> states: by name without
    /// regard to case, and two names equal but for case by node number.
    ///
    /// <para>The root here is handed children in two calls, as nothing in a walk does but the
    /// builder allows. Each call ranks its own children from zero, so placed by those ranks alone,
    /// one child would take another's place and the tree would lose it.</para>
    /// </summary>
    [Fact]
    public void PutsASnapshotsChildrenInNameOrder()
    {
        var builder = new ExploreTreeBuilder(Root);

        var first = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("b.bin", IsDirectory: false, IsLink: false, Size: 1),
            new ExploreChild("Cache", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("a.bin", IsDirectory: false, IsLink: false, Size: 1),
        ]);

        var cache = first + 1;
        var inCache = builder.AddChildren(cache, [
            new ExploreChild("zeta", IsDirectory: false, IsLink: false, Size: 1),
            new ExploreChild("Alpha", IsDirectory: false, IsLink: false, Size: 1),
            new ExploreChild("beta", IsDirectory: false, IsLink: false, Size: 1),
        ]);

        var second = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("cache", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("A.bin", IsDirectory: false, IsLink: false, Size: 1),
        ]);

        var tree = builder.Build(ExploreChildOrder.ByName);

        Assert.Equal(
            [first + 2, second + 1, first, cache, second],
            tree.ChildrenOf(ExploreTreeBuilder.RootNode).ToArray());
        Assert.Equal([inCache + 1, inCache + 2, inCache], tree.ChildrenOf(cache).ToArray());
    }
}
