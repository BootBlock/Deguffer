using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// While a redraw lands, each point of the map is answered by the drawing the screen shows there:
/// the redraw's, where its region has landed, and the last drawing's everywhere else (§7.1).
///
/// <para>A right-click picks what the menu acts on, so an answer from a drawing that is not on screen
/// at that point is a Delete aimed at a shape the user never pointed at. These run a real redraw on
/// a queued owner's thread and hold it part of the way through landing, so the state tested is the
/// one the map is in between two hand-overs.</para>
/// </summary>
public sealed class MapHitTestTests
{
    private const int Old = 1;
    private const int New = 2;

    private readonly QueuedContext _owner = new();
    private readonly CanvasRedraws _redraws;

    public MapHitTestTests() => _redraws = new CanvasRedraws(_owner);

    /// <summary>
    /// The top left region has landed and the rest have not: a point there is the new drawing's, and
    /// a point anywhere else is the old one's, because the old picture is what shows there.
    /// </summary>
    [Fact]
    public void APointResolvesAgainstThePictureOnScreenThere()
    {
        var tree = Drawings.Tree();
        var old = Drawings.Covering(tree, Old);
        var (arriving, gated) = LandTopLeft(tree);

        Assert.Equal(New, NodeAt(0.1, 0.1, old, arriving, tree));
        Assert.Equal(Old, NodeAt(0.9, 0.1, old, arriving, tree));
        Assert.Equal(Old, NodeAt(0.1, 0.9, old, arriving, tree));
        Assert.Equal(Old, NodeAt(0.9, 0.9, old, arriving, tree));

        gated.Open();
    }

    /// <summary>
    /// A redraw superseded part of the way through landing never answers again, even at the points
    /// its regions covered: they came off the screen with it.
    /// </summary>
    [Fact]
    public async Task ASupersededRedrawNeverAnswers()
    {
        var tree = Drawings.Tree();
        var old = Drawings.Covering(tree, Old);
        var (arriving, gated) = LandTopLeft(tree);

        _redraws.Cancel();

        foreach (var (x, y) in Grid())
        {
            Assert.Equal(Old, NodeAt(x, y, old, arriving, tree));
        }

        gated.Open();
        await arriving.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        _owner.RunPending();

        foreach (var (x, y) in Grid())
        {
            Assert.Equal(Old, NodeAt(x, y, old, arriving, tree));
        }
    }

    /// <summary>
    /// A redraw of a new tree landing over a drawing of the old one: the landed region answers in the
    /// new tree, and the old drawing showing round it answers nothing, because its node numbers are
    /// another tree's and the page reads every answer in the new one.
    /// </summary>
    [Fact]
    public void ADrawingOfAnOlderTreeAnswersNothing()
    {
        var older = Drawings.Tree(files: 3);
        var newer = Drawings.Tree(files: 3);
        var old = Drawings.Covering(older, Old);
        var (arriving, gated) = LandTopLeft(newer);

        Assert.Equal(New, NodeAt(0.1, 0.1, old, arriving, newer));
        Assert.Null(MapHitTest.Locate(MapViewport.Whole, 0.9, 0.9, old, arriving, newer));
        Assert.Null(MapHitTest.Locate(MapViewport.Whole, 0.5, 0.5, old, arriving: null, newer));

        gated.Open();
    }

    /// <summary>
    /// A point is taken back through where the drawing is placed on a zoomed screen, and a point past
    /// the edge of a drawing of part of the picture is over nothing.
    /// </summary>
    [Fact]
    public void APointIsFoundThroughTheZoomOnScreen()
    {
        var tree = Drawings.Tree();
        var zoomedIn = MapViewport.Anchored(2, 0, 0, 0, 0);
        var drawing = Drawings.Covering(tree, Old, zoomedIn);

        var middle = MapHitTest.Locate(zoomedIn, 0.5, 0.5, drawing, arriving: null, tree);

        Assert.NotNull(middle);
        Assert.Equal(Drawings.Size / 2f, middle.Value.X, 1);
        Assert.Equal(Drawings.Size / 2f, middle.Value.Y, 1);

        // Zoomed out to the whole picture, the drawing of its top left quarter covers only that.
        Assert.NotNull(MapHitTest.Locate(MapViewport.Whole, 0.25, 0.25, drawing, arriving: null, tree));
        Assert.Null(MapHitTest.Locate(MapViewport.Whole, 0.75, 0.75, drawing, arriving: null, tree));
    }

    /// <summary>
    /// Start a redraw of <paramref name="tree"/> that is <see cref="New"/> everywhere, and stop it
    /// with only its top left region on screen.
    /// </summary>
    private (CanvasRedraw Arriving, GatedSurface Gated) LandTopLeft(Exploring.ExploreTree tree)
    {
        var gated = new GatedSurface(Drawings.Covering(tree, New), region => region is { X: 0, Y: 0 });

        var arriving = _redraws.Start(
            () => gated,
            new byte[PixelBuffer.LengthFor(Drawings.Size, Drawings.Size)],
            Drawings.Ground,
            new ExplorePoint(1, 1),
            new RedrawRecorder());

        _owner.RunUntil(() => arriving.Landed.Count > 0, "the top left region to land");

        Assert.Equal([new CanvasRegion(0, 0, PaintOrder.RegionSize, PaintOrder.RegionSize)], arriving.Landed);

        return (arriving, gated);
    }

    private static int? NodeAt(double x, double y, ExploreSurface old, CanvasRedraw arriving, ISizedTree tree) =>
        MapHitTest.Locate(MapViewport.Whole, x, y, old, arriving, tree) is { } spot
            ? spot.Drawing.At(spot.X, spot.Y)?.Node
            : null;

    /// <summary>Points across the whole screen, a few in each region.</summary>
    private static IEnumerable<(double X, double Y)> Grid() =>
        from x in Enumerable.Range(0, 8)
        from y in Enumerable.Range(0, 8)
        select ((x + 0.5) / 8, (y + 0.5) / 8);
}
