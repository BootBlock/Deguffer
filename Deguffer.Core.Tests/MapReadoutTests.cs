using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The line under a map, and what a screen reader is told of a shape, on every page that draws one:
/// a block named alike everywhere, a node by what the page says of it, and nothing of a node the tree
/// on screen does not hold (§7.1).
/// </summary>
public sealed class MapReadoutTests
{
    private readonly ExploreTree _tree;

    private readonly int _kept;

    private readonly int _removed;

    private readonly Shapes _shapes = new();

    public MapReadoutTests()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        _kept = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("kept", IsDirectory: true, IsLink: false, Size: 0), new ExploreChild("gone.bin", IsDirectory: false, IsLink: false, Size: 10)]);
        _removed = _kept + 1;

        var whole = builder.Build(ExploreChildOrder.ByName);
        _tree = whole.Without([_removed]);
    }

    [Fact]
    public void ANodeIsSaidAsThePageSaysIt()
    {
        Assert.Equal(new MapReadout("kept", "said", string.Empty), MapReadout.For(_tree, new ExploreHit(_kept, 0), _shapes));
        Assert.Equal([_kept], _shapes.ReadoutsAsked);
    }

    /// <summary>A shape can outlive its node by a frame, and the line names nothing rather than something else.</summary>
    [Fact]
    public void ANodeTheTreeNoLongerHoldsIsSaidAsNothing()
    {
        Assert.Same(MapReadout.None, MapReadout.For(_tree, new ExploreHit(_removed, 10), _shapes));
        Assert.Empty(_shapes.ReadoutsAsked);
    }

    [Fact]
    public void ABlockIsNamedAsTheMapNamesItWithItsSizeAndThePagesNote()
    {
        var readout = MapReadout.For(_tree, new ExploreHit(ExploreTile.Unaccounted, 6_000), _shapes);

        Assert.Equal(new MapReadout(MapBlock.Unaccounted.Readout, FreeSpace.Format(6_000), "note on " + MapBlock.Unaccounted.Readout), readout);
    }

    [Fact]
    public void NothingIsSaidWithNoTreeOrNothingPointedAt()
    {
        Assert.Same(MapReadout.None, MapReadout.For<ExploreTree>(null, new ExploreHit(_kept, 0), _shapes));
        Assert.Same(MapReadout.None, MapReadout.For<ExploreTree>(null, new ExploreHit(ExploreTile.Aggregated, 10), _shapes));
        Assert.Same(MapReadout.None, MapReadout.For(_tree, null, _shapes));
        Assert.Empty(_shapes.ReadoutsAsked);
    }

    [Fact]
    public void TheNoteIsThereOnlyWhereThereIsOne()
    {
        Assert.True(new MapReadout("a", "b", "c").HasNote);
        Assert.False(new MapReadout("a", "b", string.Empty).HasNote);
    }

    [Fact]
    public void AScreenReaderIsToldOfAShapeOfTheTreeOnScreen() =>
        Assert.Equal(new MapShapeFacts("kept", "Kind", Opens: true, LowerBound: false), MapShapeFacts.For(_tree, _tree, _kept, _shapes));

    /// <summary>
    /// A replay of what grew is drawn from a tree of its own, whose node numbers mean nothing in the
    /// page's, and a removed node's shape can still be asked about.
    /// </summary>
    [Fact]
    public void AScreenReaderIsToldNothingOfAnyOtherTreeOrOfANodeItNoLongerHolds()
    {
        var other = new ExploreTreeBuilder(@"D:\").Build(ExploreChildOrder.ByName);

        Assert.Null(MapShapeFacts.For(other, _tree, _kept, _shapes));
        Assert.Null(MapShapeFacts.For(_tree, _tree, _removed, _shapes));
        Assert.Null(MapShapeFacts.For<ExploreTree>(_tree, null, _kept, _shapes));
        Assert.Null(ShownTree.Of(other, _tree, _kept));
        Assert.Same(_tree, ShownTree.Of(_tree, _tree, _kept));
    }

    private sealed class Shapes : IMapShapes<ExploreTree>
    {
        public List<int> ReadoutsAsked { get; } = [];

        public MapShapeFacts FactsOf(ExploreTree tree, int node) => new(tree.NameOf(node), "Kind", Opens: true, LowerBound: false);

        public MapReadout ReadoutOf(ExploreTree tree, int node)
        {
            ReadoutsAsked.Add(node);
            return new MapReadout(tree.NameOf(node), "said", string.Empty);
        }

        public string NoteOn(ExploreTree tree, MapBlock block) => "note on " + block.Readout;
    }
}
