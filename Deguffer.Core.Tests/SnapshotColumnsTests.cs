using System.Runtime.CompilerServices;
using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The columns the snapshots of one walk share (#738): a snapshot copies out only what it totals,
/// and a snapshot already handed over reads what it held however the walk goes on.
/// </summary>
public sealed class SnapshotColumnsTests
{
    private const string Root = @"C:\";

    /// <summary>What a snapshot allocates beside its columns: the tree, the capture and the frozen views.</summary>
    private const long Slack = 64 * 1024;

    /// <summary>
    /// The bytes a node costs in the columns each snapshot totals into: size, length, storage, flags
    /// and newest write, with its place in the child starts and the child list.
    /// </summary>
    private static readonly int TotalledBytesPerNode =
        (2 * sizeof(long))
        + Unsafe.SizeOf<FileStorage>()
        + Unsafe.SizeOf<ExploreNodeFlags>()
        + Unsafe.SizeOf<ExploreTimestamp>()
        + (2 * sizeof(int));

    private readonly ExploreTreeBuilder _builder = new(Root);
    private readonly SnapshotColumns _shared = new();
    private int _stamp;

    /// <summary>
    /// The first snapshot sizes the shared columns to its nodes and the second doubles them, so the
    /// third, a few nodes on, fits in what the second made, as most snapshots of a long walk do.
    /// </summary>
    [Fact]
    public void ASnapshotOfAGrowingWalkAllocatesOnlyTheColumnsItTotals()
    {
        AddFolders(count: 400, filesEach: 250);
        Snapshot();
        AddFolders(count: 4, filesEach: 250);
        Snapshot();
        AddFolders(count: 4, filesEach: 250);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var tree = Snapshot();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, ((long)tree.NodeCount * TotalledBytesPerNode) + Slack);
    }

    /// <summary>
    /// The second fill outgrows the first snapshot's columns, and the third writes into the second's
    /// past its count. Neither earlier snapshot sees a node it did not hold, or loses one it did.
    /// </summary>
    [Fact]
    public void AnEarlierSnapshotReadsWhatItHeldAfterLaterOnesFillTheColumnsItShares()
    {
        AddFolders(count: 10, filesEach: 20);
        var first = Snapshot();
        var firstSeen = Describe(first);

        AddFolders(count: 5, filesEach: 20);
        var second = Snapshot();
        var secondSeen = Describe(second);

        AddFolders(count: 2, filesEach: 20);
        var third = Snapshot();

        Assert.Equal(firstSeen, Describe(first));
        Assert.Equal(secondSeen, Describe(second));
        Assert.Equal(Describe(_builder.Build(ExploreChildOrder.ByName)), Describe(third));
    }

    /// <summary>
    /// A folder handed children in two calls has its children ranked again across both, in the rank
    /// column the snapshots share. Each snapshot still places every child where a tree built afresh does.
    /// </summary>
    [Fact]
    public void AFolderListedAgainAfterASnapshotIsInNameOrderInTheNext()
    {
        var folder = _builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("d")]);
        _builder.AddChildren(folder, [File("m"), File("x")]);
        Snapshot();

        _builder.AddChildren(folder, [File("a"), File("q")]);
        var second = Snapshot();

        _builder.AddChildren(folder, [File("n")]);
        var third = Snapshot();

        Assert.Equal(["a", "m", "q", "x"], Names(second, folder));
        Assert.Equal(["a", "m", "n", "q", "x"], Names(third, folder));
        Assert.Equal(Describe(_builder.Build(ExploreChildOrder.ByName)), Describe(third));
    }

    private ExploreTree Snapshot() =>
        _builder.Capture().Snapshot(ExploreChildOrder.ByName, _shared, CancellationToken.None);

    private void AddFolders(int count, int filesEach)
    {
        var first = _builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, count).Select(_ => Folder($"d{_stamp++}"))]);

        for (var folder = first; folder < first + count; folder++)
        {
            _builder.AddChildren(folder, [.. Enumerable.Range(0, filesEach).Select(i => File($"f{filesEach - i}"))]);
        }
    }

    private ExploreChild Folder(string name) =>
        new(name, IsDirectory: true, IsLink: false, Size: 0, Created: new ExploreTimestamp(++_stamp));

    // Each file's size, date and visibility differ, so a column read from the wrong slot shows.
    private ExploreChild File(string name) =>
        new(
            name,
            IsDirectory: false,
            IsLink: false,
            Size: ++_stamp,
            Created: new ExploreTimestamp(_stamp),
            Visibility: _stamp % 3 == 0 ? FileVisibility.Hidden : FileVisibility.Shown);

    private static string[] Names(ExploreTree tree, int folder) =>
        [.. tree.ChildrenOf(folder).ToArray().Select(tree.NameOf)];

    /// <summary>Every node as the tree answers for it, so two trees compare node by node.</summary>
    private static string[] Describe(ExploreTree tree) =>
        [.. Enumerable.Range(0, tree.NodeCount).Select(node =>
            $"{tree.PathOf(node)}|{tree.ParentOf(node)}|{tree.SizeOf(node)}|{tree.CreatedOf(node)}|"
            + $"{tree.VisibilityOf(node)}|{string.Join(',', tree.ChildrenOf(node).ToArray())}")];
}
