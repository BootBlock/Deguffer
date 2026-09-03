using Deguffer.Core.Exploring;

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

        var tree = builder.Build();

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

        var tree = builder.Build();

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

        var snapshot = builder.Build();

        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("b.tgz", IsDirectory: false, IsLink: false, Size: 8192),
        ]);

        Assert.Equal(4096, snapshot.TotalBytes);
        Assert.Equal(2, snapshot.NodeCount);
        Assert.Equal(12_288, builder.Build().TotalBytes);
    }
}
