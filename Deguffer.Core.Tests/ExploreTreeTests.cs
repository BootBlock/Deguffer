using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// The assembly step both of §5.5's routes share: invert the parent links, total each subtree, and
/// order every child list.
///
/// A mistake here is invisible rather than loud. The picture still draws, every rectangle still has
/// a plausible area, and the only symptom is that a directory is shown smaller than it is — which
/// is the same failure mode <see cref="MftVolumeIndexTests"/> exists for, arriving through the other
/// half of the code.
/// </summary>
public class ExploreTreeTests
{
    // A synthetic profile tree. Paths are invented rather than copied from a real machine.
    private const string Root = @"C:\Users\testuser";

    [Fact]
    public void TotalsEverySubtreeThroughEveryLevelAboveIt()
    {
        var builder = new ExploreTreeBuilder(Root);

        var top = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            Directory("AppData"),
            File("notes.txt", 50),
        ]);

        var appData = top;
        var middle = builder.AddChildren(appData, [Directory("Local")]);
        var local = middle;
        var deep = builder.AddChildren(local, [Directory("npm-cache"), File("settings.json", 200)]);

        builder.AddChildren(deep, [File("a.tgz", 4000), File("b.tgz", 6000)]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(10_000, tree.SizeOf(deep));
        Assert.Equal(10_200, tree.SizeOf(local));
        Assert.Equal(10_200, tree.SizeOf(appData));
        Assert.Equal(10_250, tree.TotalBytes);
    }

    /// <summary>
    /// Every level, not only the root's. A treemap packs each row from the largest remaining child,
    /// so a level left unsorted deeper in the tree draws a subtree whose rectangles are in no order
    /// at all — and nothing about the picture says which level went wrong.
    /// </summary>
    [Fact]
    public void OrdersEveryLevelLargestFirst()
    {
        var builder = new ExploreTreeBuilder(Root);

        var top = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            File("small.bin", 10),
            Directory("cache"),
            File("medium.bin", 500),
        ]);

        var cache = top + 1;
        builder.AddChildren(cache, [File("c.tgz", 7), File("a.tgz", 9000), File("b.tgz", 300)]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(["cache", "medium.bin", "small.bin"], NamesOfChildren(tree, tree.RootNode));
        Assert.Equal(["a.tgz", "b.tgz", "c.tgz"], NamesOfChildren(tree, cache));
    }

    /// <summary>
    /// The reason <see cref="ExploreTree"/> negates its sort keys rather than sorting ascending and
    /// reversing the span, stated as an assertion.
    ///
    /// <para>Both orderings put the largest child first, so a test that only checks sizes cannot
    /// tell them apart. What separates them is what happens to siblings of equal size: the runtime's
    /// sort falls back to a stable insertion sort below its introsort threshold, so a descending sort
    /// leaves a short directory's equal children in the order they were recorded, while reversing an
    /// ascending sort inverts them. A user sees that as a set of same-sized folders that swap places
    /// for no reason they can see.</para>
    /// </summary>
    [Fact]
    public void EqualSizedSiblingsKeepTheOrderTheyWereRecordedIn()
    {
        var builder = new ExploreTreeBuilder(Root);

        var first = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            File("alpha.bin", 4096),
            File("bravo.bin", 4096),
            File("charlie.bin", 4096),
            File("delta.bin", 4096),
        ]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(["alpha.bin", "bravo.bin", "charlie.bin", "delta.bin"], NamesOfChildren(tree, tree.RootNode));
        Assert.Equal([first, first + 1, first + 2, first + 3], tree.ChildrenOf(tree.RootNode).ToArray());
    }

    /// <summary>
    /// The same tree, assembled twice, orders identically — including a directory of equal-sized
    /// children large enough that the runtime sorts it rather than inserting it, where no ordering
    /// among the ties is prescribed and only its repeatability matters.
    /// </summary>
    [Fact]
    public void TheSameTreeAssembledTwiceOrdersEveryLevelIdentically()
    {
        Assert.Equal(OrderOfARepeatedlyBuiltTree(), OrderOfARepeatedlyBuiltTree());

        static List<string> OrderOfARepeatedlyBuiltTree()
        {
            var builder = new ExploreTreeBuilder(Root);

            var top = builder.AddChildren(ExploreTreeBuilder.RootNode, [Directory("cache"), Directory("logs")]);

            builder.AddChildren(top, [.. Enumerable.Range(0, 64).Select(i => File($"pack-{i:D2}.tgz", 4096))]);
            builder.AddChildren(top + 1, [File("a.log", 30), File("b.log", 30), File("c.log", 900)]);

            var tree = builder.Build(ExploreChildOrder.BySize);
            var order = new List<string>();

            for (var node = 0; node < tree.NodeCount; node++)
            {
                order.AddRange(NamesOfChildren(tree, node));
            }

            return order;
        }
    }

    [Fact]
    public void RebuildsTheFullPathOfADeepNodeAndAnswersTheRootWithItsOwnPath()
    {
        var builder = new ExploreTreeBuilder(Root);

        var node = ExploreTreeBuilder.RootNode;
        foreach (var component in (string[])["AppData", "Local", "npm-cache", "content-v2"])
        {
            node = builder.AddChildren(node, [Directory(component)]);
        }

        var leaf = builder.AddChildren(node, [File("sha512.tgz", 1234)]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(Root, tree.PathOf(tree.RootNode));
        Assert.Equal(Path.Combine(Root, @"AppData\Local\npm-cache\content-v2"), tree.PathOf(node));
        Assert.Equal(Path.Combine(Root, @"AppData\Local\npm-cache\content-v2\sha512.tgz"), tree.PathOf(leaf));
    }

    /// <summary>
    /// A <c>node_modules</c> tree is exactly the shape this feature exists to draw, and it is the
    /// shape that overflows a recursive traversal. The depth here is far past anything on a disk on
    /// purpose: a limit that only bites at real depths is one that ships and is met by a user.
    ///
    /// <para>Three traversals are exercised at once, because all three walk the same chain and any
    /// one of them written recursively takes the process down rather than failing a test:
    /// the depth-first ordering, the roll-up that rides on it, and <see cref="ExploreTree.PathOf"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void TotalsAndWalksATreeFarDeeperThanAnyStackWouldTake()
    {
        const int levels = 100_000;

        var builder = new ExploreTreeBuilder(Root);
        var node = ExploreTreeBuilder.RootNode;

        for (var level = 0; level < levels; level++)
        {
            node = builder.AddChildren(node, [Directory("node_modules")]);
        }

        builder.AddChildren(node, [File("index.js", 512)]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(512, tree.TotalBytes);
        Assert.Equal(levels, tree.PathOf(node).Split(Path.DirectorySeparatorChar).Count(c => c == "node_modules"));
    }

    [Fact]
    public void ReportsNoUnknownSizesForATreeThatWasFullyMeasured()
    {
        var builder = new ExploreTreeBuilder(Root);

        var top = builder.AddChildren(ExploreTreeBuilder.RootNode, [Directory("cache")]);
        builder.AddChildren(top, [File("a.tgz", 4096)]);

        Assert.False(builder.Build(ExploreChildOrder.BySize).HasUnknownSizes);
    }

    /// <summary>
    /// One node the scan could not size makes every total above it a lower bound, all the way to the
    /// root. Reporting the root's figure as a measurement is the one thing a size picture must not
    /// do — and the node that could not be sized is usually several levels down, where nothing above
    /// it would otherwise hear about it.
    /// </summary>
    [Fact]
    public void CarriesAnUnknownSizeUpEveryLevelToTheRoot()
    {
        var builder = new ExploreTreeBuilder(Root);

        var top = builder.AddChildren(ExploreTreeBuilder.RootNode, [Directory("cache"), Directory("logs")]);
        var cache = top;
        var logs = top + 1;

        var nested = builder.AddChildren(cache, [Directory("content-v2")]);
        builder.AddChildren(logs, [File("a.log", 10)]);

        builder.MarkSizeUnknown(nested);

        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.True(tree.HasUnknownSizeBelow(nested));
        Assert.True(tree.HasUnknownSizeBelow(cache));
        Assert.True(tree.HasUnknownSizes);
        Assert.False(tree.HasUnknownSizeBelow(logs));
    }

    /// <summary>
    /// A table read from a live volume holds records whose parent chain never reaches the root — a
    /// directory removed mid-read leaves its children pointing at a record that has been reused, and
    /// two of those can point at each other. Totalling one into anything would attribute bytes to a
    /// directory that does not contain them, and a cycle would stop the traversal terminating at
    /// all.
    ///
    /// <para>Built through <see cref="ExploreTree.Create"/> rather than the builder, because the
    /// builder cannot express it: everything it records hangs off something it already recorded.
    /// </para>
    /// </summary>
    [Fact]
    public void CountsNothingThatCannotBeReachedFromTheRoot()
    {
        // 3 and 4 are each other's parent: present, sized, and reachable from nothing. 5 is a slot
        // no reader filled, which is what a free record looks like.
        var tree = ExploreTree.Create(
            Root,
            rootNode: 0,
            names: ["testuser", "cache", "a.tgz", "orphan", "orphan-parent", ""],
            parents: [0, 0, 1, 4, 3, 0],
            sizes: [0, 0, 100, 500, 700, 900],
            isDirectory: [true, true, false, true, true, false],
            isLink: [false, false, false, false, false, false],
            sizeUnknown: [false, false, false, false, false, false],
            present: [true, true, true, true, true, false],
            childOrder: ExploreChildOrder.BySize);

        Assert.Equal(100, tree.TotalBytes);
        Assert.Equal(["cache"], NamesOfChildren(tree, tree.RootNode));
    }

    /// <summary>
    /// What <see cref="ExploreChildOrder.ByName"/> is for, as an assertion: two snapshots of one
    /// scan, taken as a directory below fills in, order the root's children identically.
    /// </summary>
    [Fact]
    public void ANameOrderedTreeKeepsItsSiblingsWhereTheyAreAsTheyGrow()
    {
        var builder = GrowingScan(out var cache);

        var early = builder.Build(ExploreChildOrder.ByName);

        // The walk reaches the cache, which turns out to hold more than everything else together.
        builder.AddChildren(cache, [File("big.bin", 9_000_000)]);

        var later = builder.Build(ExploreChildOrder.ByName);

        Assert.Equal(["apps", "cache", "zzz.bin"], NamesOfChildren(early, early.RootNode));
        Assert.Equal(["apps", "cache", "zzz.bin"], NamesOfChildren(later, later.RootNode));
        Assert.Equal(ExploreChildOrder.ByName, later.ChildOrder);
    }

    /// <summary>
    /// The same two snapshots under a size order, which is the churn the name order exists to stop.
    /// Stated as its own test rather than left implied, because a stability test that would pass
    /// against either order proves nothing — every sibling here changes place between the two.
    /// </summary>
    [Fact]
    public void ASizeOrderedTreeRearrangesItselfAsItsChildrenGrow()
    {
        var builder = GrowingScan(out var cache);

        var early = builder.Build(ExploreChildOrder.BySize);

        builder.AddChildren(cache, [File("big.bin", 9_000_000)]);

        var later = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(["zzz.bin", "apps", "cache"], NamesOfChildren(early, early.RootNode));
        Assert.Equal(["cache", "zzz.bin", "apps"], NamesOfChildren(later, later.RootNode));
        Assert.Equal(ExploreChildOrder.BySize, later.ChildOrder);
    }

    /// <summary>
    /// Compared without regard to case, because a directory holding <c>Apps</c> and <c>zzz.bin</c>
    /// drawn in ordinal order puts every capitalised name before every lower-case one — which reads
    /// as no order at all to somebody looking at the list.
    /// </summary>
    [Fact]
    public void ANameOrderIgnoresCase()
    {
        var builder = new ExploreTreeBuilder(Root);

        // Every capital letter sorts ahead of every lower-case one ordinally, so this is the
        // order that separates the two comparisons rather than one they agree on.
        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            File("Zebra.bin", 10),
            File("apples.bin", 10),
            File("mangoes.bin", 10),
            File("Bananas.bin", 10),
        ]);

        var tree = builder.Build(ExploreChildOrder.ByName);

        Assert.Equal(
            ["apples.bin", "Bananas.bin", "mangoes.bin", "Zebra.bin"],
            NamesOfChildren(tree, tree.RootNode));
    }

    /// <summary>
    /// A scan part way through a directory that is about to dwarf its siblings. The root holds one
    /// of each thing a walk records — a directory already measured, a directory not yet descended
    /// into, and a file — and <paramref name="cache"/> is the one still to fill in.
    /// </summary>
    private static ExploreTreeBuilder GrowingScan(out int cache)
    {
        var builder = new ExploreTreeBuilder(Root);

        var top = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            Directory("apps"),
            Directory("cache"),
            File("zzz.bin", 5000),
        ]);

        builder.AddChildren(top, [File("a.bin", 100)]);

        cache = top + 1;
        return builder;
    }

    private static ExploreChild Directory(string name) =>
        new(name, IsDirectory: true, IsLink: false, Size: 0);

    private static ExploreChild File(string name, long size) =>
        new(name, IsDirectory: false, IsLink: false, size);

    private static List<string> NamesOfChildren(ExploreTree tree, int node)
    {
        var names = new List<string>();

        foreach (var child in tree.ChildrenOf(node))
        {
            names.Add(tree.NameOf(child));
        }

        return names;
    }
}
