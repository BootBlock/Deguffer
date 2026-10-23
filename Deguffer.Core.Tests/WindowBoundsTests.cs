using Deguffer.Core.Configuration;

namespace Deguffer.Core.Tests;

/// <summary>
/// A remembered placement is applied to a desktop that has since changed, so the cases worth the
/// test are the ones where it no longer fits: a display that was unplugged, one that is smaller
/// than the one before it, and a file somebody edited by hand.
///
/// Every one of them has to land on a window the user can reach. A window placed where no display
/// reaches, or made taller than the desktop, cannot be dragged back — the title bar it would be
/// dragged by is the part that is off screen.
/// </summary>
public sealed class WindowBoundsTests
{
    /// <summary>A 1920x1080 display with a taskbar along the bottom.</summary>
    private static readonly WindowBounds Work = new(0, 0, 1920, 1032);

    private const int MinimumWidth = 880;
    private const int MinimumHeight = 520;

    [Fact]
    public void LeavesAPlacementThatAlreadyFits()
    {
        var placement = new WindowBounds(412, 96, 1000, 700);

        Assert.Equal(placement, placement.Within(Work, MinimumWidth, MinimumHeight));
    }

    /// <summary>
    /// The display it was left on is narrower than the one it was left on last time — the ordinary
    /// laptop-undocked case. The window comes back against the right edge rather than half off it.
    /// </summary>
    [Fact]
    public void PullsAWindowBackFromPastTheRightEdge()
    {
        var placed = new WindowBounds(1700, 200, 1000, 700).Within(Work, MinimumWidth, MinimumHeight);

        Assert.Equal(920, placed.X);
        Assert.Equal(200, placed.Y);
        Assert.Equal(1000, placed.Width);
    }

    [Fact]
    public void PullsAWindowBackFromAboveTheWorkArea()
    {
        var placed = new WindowBounds(-400, -300, 1000, 700).Within(Work, MinimumWidth, MinimumHeight);

        Assert.Equal(0, placed.X);
        Assert.Equal(0, placed.Y);
    }

    /// <summary>
    /// A second display to the left of the primary one has a negative origin, and a placement on it
    /// is not an off-screen placement. Clamping to zero rather than to the work area's own origin
    /// would drag every such window onto the primary display.
    /// </summary>
    [Fact]
    public void KeepsAPlacementOnADisplayLeftOfThePrimaryOne()
    {
        var work = new WindowBounds(-1920, 0, 1920, 1032);
        var placement = new WindowBounds(-1500, 120, 1000, 700);

        Assert.Equal(placement, placement.Within(work, MinimumWidth, MinimumHeight));
    }

    [Fact]
    public void ShrinksAWindowLargerThanTheWorkArea()
    {
        var placed = new WindowBounds(0, 0, 4000, 3000).Within(Work, MinimumWidth, MinimumHeight);

        Assert.Equal(new WindowBounds(0, 0, 1920, 1032), placed);
    }

    /// <summary>
    /// A hand-edited size below the floor the window procedure enforces. Applying it would open a
    /// window the layout collides in, and one the user never chose.
    /// </summary>
    [Fact]
    public void RaisesASizeBelowTheFloor()
    {
        var placed = new WindowBounds(100, 100, 200, 150).Within(Work, MinimumWidth, MinimumHeight);

        Assert.Equal(MinimumWidth, placed.Width);
        Assert.Equal(MinimumHeight, placed.Height);
    }

    /// <summary>
    /// A display too small for the minimum layout. The work area wins, because a cramped window can
    /// be resized and a window taller than the desktop cannot.
    /// </summary>
    [Fact]
    public void LetsAWorkAreaSmallerThanTheFloorWin()
    {
        var work = new WindowBounds(0, 0, 800, 480);

        var placed = new WindowBounds(0, 0, 1000, 700).Within(work, MinimumWidth, MinimumHeight);

        Assert.Equal(new WindowBounds(0, 0, 800, 480), placed);
    }

    /// <summary>
    /// The file is hand-editable, so a coordinate can be anything an <see cref="int"/> holds. Both
    /// ends of the range have to land on a reachable window rather than on whatever the arithmetic
    /// happens to produce.
    /// </summary>
    [Fact]
    public void SurvivesCoordinatesAtTheEndsOfTheRange()
    {
        var far = new WindowBounds(int.MaxValue, int.MaxValue, 1000, 700)
            .Within(Work, MinimumWidth, MinimumHeight);

        Assert.Equal(920, far.X);
        Assert.Equal(332, far.Y);

        var near = new WindowBounds(int.MinValue, int.MinValue, 1000, 700)
            .Within(Work, MinimumWidth, MinimumHeight);

        Assert.Equal(0, near.X);
        Assert.Equal(0, near.Y);
    }

    /// <summary>
    /// An extent at the end of the range, which the same hand-edited file can carry. It is capped
    /// to the work area like any other oversized window.
    /// </summary>
    [Fact]
    public void SurvivesAnExtentAtTheEndOfTheRange()
    {
        var placed = new WindowBounds(0, 0, int.MaxValue, int.MaxValue)
            .Within(Work, MinimumWidth, MinimumHeight);

        Assert.Equal(new WindowBounds(0, 0, 1920, 1032), placed);
    }

    /// <summary>A tool window opens to the right of its owner where the display has room for it, level with its top.</summary>
    [Fact]
    public void AToolWindowOpensToTheRightWhereThereIsRoom()
    {
        var owner = new WindowBounds(100, 50, 1000, 700);

        Assert.Equal(new WindowBounds(1108, 50, 520, 820), owner.Beside(Work, 520, 820, gap: 8, drop: 96));
    }

    /// <summary>To the left where only that side has room.</summary>
    [Fact]
    public void AToolWindowOpensToTheLeftWhereOnlyThatSideHasRoom()
    {
        var owner = new WindowBounds(800, 50, 1000, 700);

        Assert.Equal(new WindowBounds(272, 50, 520, 820), owner.Beside(Work, 520, 820, gap: 8, drop: 96));
    }

    /// <summary>
    /// Over the owner's right-hand edge, below its toolbar, where neither side has room: a maximised
    /// owner, which is where most readers keep the map.
    /// </summary>
    [Fact]
    public void AToolWindowOverlapsAMaximisedOwnerBelowItsToolbar()
    {
        Assert.Equal(new WindowBounds(1376, 96, 520, 820), Work.Beside(Work, 520, 820, gap: 8, drop: 96));
    }

    /// <summary>
    /// Never past the display, at any edge. An owner low on the screen would put the window's bottom
    /// off it, and a display too small for the window gives a smaller window rather than one whose
    /// title bar cannot be reached.
    /// </summary>
    [Fact]
    public void AToolWindowStaysOnTheDisplay()
    {
        var low = new WindowBounds(100, 600, 1000, 400).Beside(Work, 520, 820, gap: 8, drop: 96);

        Assert.Equal(new WindowBounds(1108, 212, 520, 820), low);

        var small = new WindowBounds(0, 0, 800, 600);

        Assert.Equal(new WindowBounds(0, 0, 800, 600), small.Beside(small, 1000, 900, gap: 8, drop: 96));
    }
}
