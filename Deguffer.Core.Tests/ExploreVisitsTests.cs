using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// Back and Forward over the places the reader has been in a scanned tree, which retrace it the way
/// a browser's history does.
/// </summary>
public sealed class ExploreVisitsTests
{
    private static readonly VolumeSpace Volume = new(TotalBytes: 100_000, FreeBytes: 40_000);

    private static readonly MapViewport Zoomed = MapViewport.Fitting(new MapFrame(0.25, 0.25, 0.5, 0.5), MapCeiling.Least);

    private static readonly Func<int, bool> NothingRemoved = _ => false;

    /// <summary>The treemap on screen, where a zoom is part of where the reader is.</summary>
    private const bool Zooms = true;

    [Fact]
    public void BackReturnsToWhereTheReaderWasAndForwardReturnsToWhereTheyWent()
    {
        var (tree, outer, inner) = Tree(@"D:\");
        var visits = new ExploreVisits();
        var top = At(ExplorePosition.Top(tree));

        visits.Leave(top);
        visits.Leave(At(outer));

        var back = visits.Back(At(inner), tree, Volume, Zooms, NothingRemoved);
        Assert.Equal(At(outer), back);

        var further = visits.Back(back!.Value, tree, Volume, Zooms, NothingRemoved);
        Assert.Equal(top, further);
        Assert.False(visits.CanGoBack(top, tree, Volume, Zooms, NothingRemoved));

        var forward = visits.Forward(top, tree, Volume, Zooms, NothingRemoved);
        Assert.Equal(At(outer), forward);
        Assert.Equal(At(inner), visits.Forward(forward!.Value, tree, Volume, Zooms, NothingRemoved));
        Assert.False(visits.CanGoForward(At(inner), tree, Volume, Zooms, NothingRemoved));
    }

    /// <summary>
    /// A step somewhere new after going back forgets what lay ahead, as a browser does: it was ahead
    /// of a place the reader has now left by another route.
    /// </summary>
    [Fact]
    public void AStepSomewhereNewForgetsWhatLayAhead()
    {
        var (tree, outer, inner) = Tree(@"D:\");
        var visits = new ExploreVisits();
        var top = At(ExplorePosition.Top(tree));

        visits.Leave(top);
        visits.Back(At(outer), tree, Volume, Zooms, NothingRemoved);
        Assert.True(visits.CanGoForward(top, tree, Volume, Zooms, NothingRemoved));

        visits.Leave(top);

        Assert.False(visits.CanGoForward(At(inner), tree, Volume, Zooms, NothingRemoved));
        Assert.Null(visits.Forward(At(inner), tree, Volume, Zooms, NothingRemoved));
    }

    /// <summary>
    /// A zoom to a shape is a step of its own, so Back undoes it in the same place, and Forward zooms
    /// in again.
    /// </summary>
    [Fact]
    public void AZoomIsAStepOfItsOwn()
    {
        var (tree, outer, _) = Tree(@"D:\");
        var visits = new ExploreVisits();
        var whole = At(outer);
        var zoomed = whole with { Viewport = Zoomed };

        visits.Leave(whole);

        Assert.Equal(whole, visits.Back(zoomed, tree, Volume, Zooms, NothingRemoved));
        Assert.Equal(zoomed, visits.Forward(whole, tree, Volume, Zooms, NothingRemoved));
    }

    /// <summary>
    /// A folder removed since is passed over and forgotten. Shown, it would draw a folder that is no
    /// longer on the disk, which the page refuses to open by any other route.
    /// </summary>
    [Fact]
    public void AStepIntoARemovedFolderIsPassedOverAndForgotten()
    {
        var (tree, outer, inner) = Tree(@"D:\");
        var visits = new ExploreVisits();
        var top = At(ExplorePosition.Top(tree));
        Func<int, bool> outerRemoved = node => node == outer.Node || node == inner.Node;

        visits.Leave(top);
        visits.Leave(At(outer));
        visits.Leave(At(inner));

        var current = At(ExplorePosition.Inside(tree.RootNode));

        Assert.Equal(top, visits.Back(current, tree, Volume, Zooms, outerRemoved));

        // Forward goes back to where Back left, not to the folders passed over on the way.
        Assert.Equal(current, visits.Forward(top, tree, Volume, Zooms, outerRemoved));
        Assert.False(visits.CanGoForward(current, tree, Volume, Zooms, NothingRemoved));
    }

    /// <summary>
    /// Nothing leads anywhere where every step is gone, and asking changes nothing.
    /// </summary>
    [Fact]
    public void BackWithNowhereToGoChangesNothing()
    {
        var (tree, outer, _) = Tree(@"D:\");
        var visits = new ExploreVisits();
        var top = At(ExplorePosition.Top(tree));

        visits.Leave(At(outer));

        Assert.False(visits.CanGoBack(top, tree, Volume, Zooms, _ => true));
        Assert.Null(visits.Back(top, tree, Volume, Zooms, _ => true));
        Assert.False(visits.CanGoForward(top, tree, Volume, Zooms, NothingRemoved));
        Assert.True(visits.CanGoBack(top, tree, Volume, Zooms, NothingRemoved));
    }

    /// <summary>
    /// The place the reader is standing in is passed over: a step there would go nowhere. A scan of a
    /// folder draws its root the same from the top and opened, so the two are one place there.
    /// </summary>
    [Fact]
    public void ThePlaceTheReaderIsInIsPassedOver()
    {
        var (tree, _, _) = Tree(@"D:\Projects");
        var visits = new ExploreVisits();

        visits.Leave(At(ExplorePosition.Top(tree)));

        var opened = At(ExplorePosition.Inside(tree.RootNode));

        Assert.False(visits.CanGoBack(opened, tree, VolumeSpace.None, Zooms, NothingRemoved));
        Assert.True(visits.CanGoBack(opened, tree, Volume, Zooms, NothingRemoved));
    }

    /// <summary>
    /// Only the nearest steps are kept in either direction, the oldest forgotten first, so Back walks
    /// as far as the earliest step still kept and Forward walks all the way back again.
    /// </summary>
    [Fact]
    public void OnlyTheNearestStepsAreKept()
    {
        var (tree, outer, inner) = Tree(@"D:\");
        var visits = new ExploreVisits();
        var taken = ExploreVisits.Depth + 10;

        for (var i = 0; i < taken; i++)
        {
            visits.Leave(At(outer) with { Viewport = ZoomedBy(i) });
        }

        var current = At(inner);
        var steps = 0;

        while (visits.Back(current, tree, Volume, Zooms, NothingRemoved) is { } back)
        {
            current = back;
            steps++;
        }

        Assert.Equal(ExploreVisits.Depth, steps);
        Assert.Equal(ZoomedBy(taken - ExploreVisits.Depth), current.Viewport);

        steps = 0;

        while (visits.Forward(current, tree, Volume, Zooms, NothingRemoved) is { } forward)
        {
            current = forward;
            steps++;
        }

        Assert.Equal(ExploreVisits.Depth, steps);
        Assert.Equal(At(inner), current);
    }

    /// <summary>
    /// On a view that shows no zoom, a step that differs from here only by its zoom would change
    /// nothing on screen, so it is passed over.
    /// </summary>
    [Fact]
    public void AZoomIsNoStepOnAViewThatShowsNone()
    {
        var (tree, outer, _) = Tree(@"D:\");
        var visits = new ExploreVisits();
        var whole = At(outer);

        visits.Leave(whole with { Viewport = Zoomed });

        Assert.True(visits.CanGoBack(whole, tree, Volume, Zooms, NothingRemoved));
        Assert.False(visits.CanGoBack(whole, tree, Volume, zooms: false, NothingRemoved));
        Assert.Null(visits.Back(whole, tree, Volume, zooms: false, NothingRemoved));
    }

    /// <summary>
    /// A rescan of the same place keeps every step whose folder is still there, at the whole picture,
    /// because a zoom is a part of a layout the arriving tree no longer has.
    /// </summary>
    [Fact]
    public void ARescanKeepsTheStepsStillThereAtTheWholePicture()
    {
        var (first, outer, _) = Tree(@"D:\");
        var (second, _, _) = Tree(@"d:\");
        var visits = new ExploreVisits();
        var top = At(ExplorePosition.Top(first));

        visits.Leave(top with { Viewport = Zoomed });
        visits.Leave(At(outer) with { Viewport = Zoomed });

        visits.Carry(first, second);

        var current = At(ExplorePosition.Inside(second.RootNode));

        Assert.Equal(At(outer), visits.Back(current, second, Volume, Zooms, NothingRemoved));
        Assert.Equal(top, visits.Back(At(outer), second, Volume, Zooms, NothingRemoved));
    }

    /// <summary>
    /// A step whose folder the arriving tree does not have is forgotten. A node number that stays is
    /// not enough: here the arriving tree's node is another folder.
    /// </summary>
    [Fact]
    public void ARescanForgetsWhatHasGone()
    {
        var (first, outer, _) = Tree(@"D:\");
        var visits = new ExploreVisits();
        var top = At(ExplorePosition.Top(first));

        visits.Leave(top);
        visits.Leave(top with { Viewport = Zoomed });
        visits.Leave(At(outer));

        var builder = new ExploreTreeBuilder(@"D:\");
        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("elsewhere", IsDirectory: true, IsLink: false, Size: 10)]);
        var arriving = builder.Build(ExploreChildOrder.BySize);

        visits.Carry(first, arriving);

        var current = At(ExplorePosition.Inside(arriving.RootNode));

        Assert.Equal(top, visits.Back(current, arriving, Volume, Zooms, NothingRemoved));
        Assert.False(visits.CanGoBack(top, arriving, Volume, Zooms, NothingRemoved));
    }

    [Fact]
    public void ATreeRootedSomewhereElseForgetsEveryStep()
    {
        var (first, outer, _) = Tree(@"D:\");
        var (elsewhere, _, _) = Tree(@"E:\");
        var visits = new ExploreVisits();

        visits.Leave(At(ExplorePosition.Top(first)));
        visits.Leave(At(outer));

        visits.Carry(first, elsewhere);

        Assert.False(visits.CanGoBack(At(ExplorePosition.Inside(elsewhere.RootNode)), elsewhere, Volume, Zooms, NothingRemoved));
    }

    /// <summary>The same tree handed over again is no replacement, and the zooms on its steps stand.</summary>
    [Fact]
    public void TheSameTreeKeepsItsZooms()
    {
        var (tree, outer, _) = Tree(@"D:\");
        var visits = new ExploreVisits();
        var zoomed = At(outer) with { Viewport = Zoomed };

        visits.Leave(zoomed);
        visits.Carry(tree, tree);

        Assert.Equal(zoomed, visits.Back(At(outer), tree, Volume, Zooms, NothingRemoved));
    }

    private static ExploreVisit At(ExplorePosition position) => new(position, MapViewport.Whole);

    /// <summary>A zoom told apart from every other <paramref name="step"/> by how far it is zoomed.</summary>
    private static MapViewport ZoomedBy(int step) =>
        MapViewport.Fitting(new MapFrame(0, 0, 1.0 / (step + 2), 1.0 / (step + 2)), MapCeiling.Least);

    /// <summary>A root holding a folder holding a folder holding a file.</summary>
    private static (ExploreTree Tree, ExplorePosition Outer, ExplorePosition Inner) Tree(string root)
    {
        var builder = new ExploreTreeBuilder(root);
        var outer = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("outer", IsDirectory: true, IsLink: false, Size: 0)]);
        var inner = builder.AddChildren(outer, [new ExploreChild("inner", IsDirectory: true, IsLink: false, Size: 0)]);

        builder.AddChildren(inner, [new ExploreChild("file", IsDirectory: false, IsLink: false, Size: 60_000)]);

        return (builder.Build(ExploreChildOrder.BySize), ExplorePosition.Inside(outer), ExplorePosition.Inside(inner));
    }
}
