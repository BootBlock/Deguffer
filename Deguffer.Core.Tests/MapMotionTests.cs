using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// What is moving the map's camera. A drag cut short by something else leaves a stretch the
/// tracker's reports know nothing of, so what the map takes the screen to show would be out by it
/// and a click would resolve against a shape the screen does not show there (§7.1).
/// </summary>
public sealed class MapMotionTests
{
    private static readonly MapTracking Start = new(2400, 1350, 4, default);

    [Fact]
    public void AJumpAGlideOrAHandCutsADragShort()
    {
        var dragging = MapMotion.Dragged(NewDrag());

        Assert.True(dragging.CutsShort(MapMotion.Jumped), "a jump during a drag left its stretch");
        Assert.True(dragging.CutsShort(MapMotion.Tracked), "a glide or a hand during a drag left its stretch");
        Assert.True(dragging.CutsShort(MapMotion.Still), "a drag ended by a reset left its stretch");
        Assert.True(dragging.CutsShort(MapMotion.Dragged(NewDrag())), "a new drag kept the old one's stretch");
    }

    [Fact]
    public void ADragCarryingOnIsNotCutShort()
    {
        var drag = NewDrag();

        Assert.False(MapMotion.Dragged(drag).CutsShort(MapMotion.Dragged(drag)));
    }

    [Fact]
    public void NothingButADragCanBeCutShort()
    {
        foreach (var before in new[] { MapMotion.Still, MapMotion.Jumped, MapMotion.Tracked })
        {
            Assert.False(before.CutsShort(MapMotion.Dragged(NewDrag())), $"{before.By} was taken as a drag");
            Assert.False(before.CutsShort(MapMotion.Still), $"{before.By} was taken as a drag");
        }
    }

    /// <summary>Only a jump waits to rest, so a run of them is drawn once; the rest come to rest as the tracker reports it.</summary>
    [Fact]
    public void OnlyAJumpSettles()
    {
        Assert.True(MapMotion.Jumped.Settles);
        Assert.False(MapMotion.Still.Settles);
        Assert.False(MapMotion.Tracked.Settles);
        Assert.False(MapMotion.Dragged(NewDrag()).Settles);
    }

    private static MapStretch NewDrag() => new(Start, 1600, 900, elastic: true);
}
