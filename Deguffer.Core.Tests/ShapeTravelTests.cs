using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where each shape's sprite is at the start and the end of the map's move between two layouts: a
/// shape that moved or appeared shows the later drawing, scaled from where it was to where it is, and
/// one that went shows the earlier drawing, shrinking to nothing at its middle.
/// </summary>
public sealed class ShapeTravelTests
{
    private const int Width = 1000;

    private const int Height = 500;

    private const double Precision = 1e-9;

    [Fact]
    public void AMovedShapeShowsTheLaterDrawingScaledFromWhereItWasToWhereItIs()
    {
        var change = new ShapeChange(ShapeChangeKind.Moved, 7, new MapFrame(0.1, 0.2, 0.2, 0.4), new MapFrame(0.5, 0.1, 0.4, 0.2));

        var travel = ShapeTravel.Of(change, Width, Height)!.Value;

        Assert.False(travel.ShowsEarlier);

        // The later drawing's rectangle, read where the shape is drawn in it.
        AssertClose(new MapFrame(500, 50, 400, 100), travel.Source);

        // Starts over where it was: at its old corner, half as wide and twice as tall as it ends.
        Assert.Equal(new SpritePlacement(100, 100, 0.5, 2), Rounded(travel.Start));
        Assert.Equal(new SpritePlacement(500, 50, 1, 1), Rounded(travel.End));
    }

    /// <summary>A shape that appeared grows from nothing at the middle of where it is drawn.</summary>
    [Fact]
    public void AShapeThatAppearedGrowsFromTheMiddleOfItsPlace()
    {
        var to = new MapFrame(0.2, 0.2, 0.2, 0.4);
        var change = new ShapeChange(ShapeChangeKind.Appeared, 7, new MapFrame(0.3, 0.4, 0, 0), to);

        var travel = ShapeTravel.Of(change, Width, Height)!.Value;

        Assert.False(travel.ShowsEarlier);
        Assert.Equal(new SpritePlacement(300, 200, 0, 0), Rounded(travel.Start));
        Assert.Equal(new SpritePlacement(200, 100, 1, 1), Rounded(travel.End));
    }

    /// <summary>A shape that went shows the earlier drawing, where it was, and shrinks to nothing at its middle.</summary>
    [Fact]
    public void AShapeThatWentShowsTheEarlierDrawingShrinkingToItsMiddle()
    {
        var from = new MapFrame(0.2, 0.2, 0.2, 0.4);
        var change = new ShapeChange(ShapeChangeKind.Disappeared, 7, from, new MapFrame(0.3, 0.4, 0, 0));

        var travel = ShapeTravel.Of(change, Width, Height)!.Value;

        Assert.True(travel.ShowsEarlier);
        AssertClose(new MapFrame(200, 100, 200, 200), travel.Source);
        Assert.Equal(new SpritePlacement(200, 100, 1, 1), Rounded(travel.Start));
        Assert.Equal(new SpritePlacement(300, 200, 0, 0), Rounded(travel.End));
    }

    /// <summary>A shape with no area at the end of its move has nothing to show, and is left to the crossfade.</summary>
    [Fact]
    public void AShapeWithNoAreaToShowHasNoTravel()
    {
        Assert.Null(ShapeTravel.Of(new ShapeChange(ShapeChangeKind.Moved, 7, new MapFrame(0.1, 0.1, 0.1, 0.1), new MapFrame(0.5, 0.5, 0, 0.1)), Width, Height));
        Assert.Null(ShapeTravel.Of(new ShapeChange(ShapeChangeKind.Disappeared, 7, new MapFrame(0.1, 0.1, 0.1, 0), new MapFrame(0.15, 0.1, 0, 0)), Width, Height));
    }

    /// <summary>
    /// Between two drawings of one canvas at one zoom, each shape that moved and has an area to show
    /// travels, as its change says.
    /// </summary>
    [Fact]
    public void BetweenTwoDrawingsOfOneCanvasEachShapeThatMovedTravels()
    {
        var earlier = Drawing(Scan(("alpha", 300), ("beta", 200), ("gamma", 100)), Width, Height, MapViewport.Whole);
        var later = Drawing(Scan(("alpha", 300), ("beta", 200), ("gamma", 260)), Width, Height, MapViewport.Whole);

        var travels = ShapeTravel.Between(earlier, later);

        Assert.NotEmpty(travels);
        Assert.Equal(
            ShapeChanges.Between(earlier, later).Select(change => ShapeTravel.Of(change, Width, Height)).OfType<ShapeTravel>(),
            travels);
    }

    /// <summary>
    /// Two drawings of another size, or at another zoom, are not where the same rectangles of the
    /// screen say, so nothing travels between them, though their shapes did change.
    /// </summary>
    [Fact]
    public void DrawingsOfAnotherCanvasOrAnotherZoomTravelNothing()
    {
        var before = Scan(("alpha", 300), ("beta", 200), ("gamma", 100));
        var after = Scan(("alpha", 300), ("beta", 200), ("gamma", 260));
        var earlier = Drawing(before, Width, Height, MapViewport.Whole);
        var zoomed = MapViewport.Fitting(new MapFrame(0.25, 0.25, 0.5, 0.5), 64);

        foreach (var later in new[]
        {
            Drawing(after, Width + 200, Height, MapViewport.Whole),
            Drawing(after, Width, Height + 200, MapViewport.Whole),
            Drawing(after, Width, Height, zoomed),
        })
        {
            Assert.NotEmpty(ShapeChanges.Between(earlier, later));
            Assert.Empty(ShapeTravel.Between(earlier, later));
        }
    }

    private static ExploreSurface Drawing(ISizedTree tree, int width, int height, MapViewport viewport) =>
        ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Treemap, width, height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), MapLook.Default, VolumeSpace.None, viewport);

    /// <summary>Folders of one file each, sized as given, met in the order given.</summary>
    private static ExploreTree Scan(params (string Name, long Size)[] folders)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. folders.Select(folder => new ExploreChild(folder.Name, IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < folders.Length; i++)
        {
            builder.AddChildren(first + i, [new ExploreChild($"{folders[i].Name}.bin", IsDirectory: false, IsLink: false, Size: folders[i].Size * 1000)]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static SpritePlacement Rounded(SpritePlacement placement) =>
        new(Math.Round(placement.X, 9), Math.Round(placement.Y, 9), Math.Round(placement.ScaleX, 9), Math.Round(placement.ScaleY, 9));

    private static void AssertClose(MapFrame expected, MapFrame actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }
}
