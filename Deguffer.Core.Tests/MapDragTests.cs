using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether a press on the map is a click or a drag. A click picks what the menu and Delete then act
/// on, so a click taken for a drag leaves the last pick in place, and a drag taken for a click picks
/// whatever the hand let go over (§7.1).
/// </summary>
public sealed class MapDragTests
{
    [Fact]
    public void APressThatWandersLessThanTheThresholdStaysAClick()
    {
        var drag = new MapDrag();

        drag.Press(100, 100, movable: true);

        Assert.Null(drag.Move(100 + MapDrag.Threshold - 0.5, 100 - MapDrag.Threshold + 0.5, held: true));
        Assert.False(drag.IsDragging);
        Assert.False(drag.Release());
        Assert.False(drag.Dragged);
    }

    /// <summary>
    /// Past the threshold it drags by the whole distance from the press, so the picture catches up
    /// with the hand, and every move after that drags however small it is.
    /// </summary>
    [Fact]
    public void APressThatGoesFarEnoughDragsAllTheWayFromWhereItWentDown()
    {
        var drag = new MapDrag();

        drag.Press(100, 100, movable: true);

        Assert.Equal((MapDrag.Threshold, -1), drag.Move(100 + MapDrag.Threshold, 99, held: true));
        Assert.Equal((0.5, 0.5), drag.Move(100 + MapDrag.Threshold + 0.5, 99.5, held: true));
        Assert.True(drag.Release());
        Assert.True(drag.Dragged);
    }

    /// <summary>
    /// The whole picture has nowhere to go. Armed there, a click that wandered would show a drag that
    /// moved nothing and swallow the click, leaving the last pick in place for Delete.
    /// </summary>
    [Fact]
    public void APressOnAPictureThatCannotMoveNeverDrags()
    {
        var drag = new MapDrag();

        drag.Press(100, 100, movable: false);

        Assert.Null(drag.Move(300, 300, held: true));
        Assert.False(drag.Dragged);
    }

    [Fact]
    public void TheNextPressIsAClickAgain()
    {
        var drag = new MapDrag();

        drag.Press(100, 100, movable: true);
        drag.Move(200, 200, held: true);
        drag.Release();

        drag.Press(50, 50, movable: true);

        Assert.False(drag.Dragged);
    }

    /// <summary>
    /// A button let go where the map never heard it, before any drag captured the pointer, ends the
    /// press: a later move with no button down must not drag.
    /// </summary>
    [Fact]
    public void AMoveWithTheButtonUpEndsThePress()
    {
        var drag = new MapDrag();

        drag.Press(100, 100, movable: true);

        Assert.Null(drag.Move(100, 100, held: false));
        Assert.Null(drag.Move(300, 300, held: true));
        Assert.False(drag.Dragged);
    }
}
