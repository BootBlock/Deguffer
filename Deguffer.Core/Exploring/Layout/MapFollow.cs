using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Where a zoomed map's camera goes when the place it shows is laid out again, so the reader stays
/// on what they zoomed into: the shape at the middle of the screen stays at the middle of the screen,
/// wherever the new layout put it (#290).
///
/// <para>The Memory page reads the machine every couple of seconds, and a treemap ordered by size
/// moves a process that grew past a sibling to the other side of its parent. A camera held where it
/// was in the picture would then show whatever moved into that place, and a reader who zoomed into
/// one service host to watch it would lose it at the next reading.</para>
///
/// <para>The point under the middle of the screen is kept at the same place inside the deepest shape
/// it was over, found again in the new layout by what it is (<see cref="ITrackedTree"/>). A shape
/// that went, or that the new layout no longer draws on its own, hands over to the folder it is in.
/// The zoom is the reader's, so it is never changed: only the camera's position follows.</para>
/// </summary>
public static class MapFollow
{
    /// <summary>
    /// The viewport that keeps the reader on the same thing after <paramref name="earlier"/> is laid
    /// out again as <paramref name="later"/>: two drawings of the same place at the same zoom, which
    /// fills the screen. The zoom both were drawn at where nothing can be followed: a whole picture, two
    /// trees of different things, or nothing under the middle found in both.
    /// </summary>
    public static MapViewport Onward(ExploreSurface earlier, ExploreSurface later)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);

        if (earlier.Viewport is not { IsWhole: false } viewport
            || later.Viewport != viewport
            || earlier.Tree is not ITrackedTree before
            || before.CounterpartsIn(later.Tree) is not { } onward)
        {
            return earlier.Viewport ?? MapViewport.Whole;
        }

        // Deepest last: a layout emits a folder before what it holds, and every shape under a point
        // holds the next one under it.
        var middle = (X: earlier.Width / 2f, Y: earlier.Height / 2f);
        var under = new List<ExploreTile>();

        foreach (var tile in earlier.Tiles)
        {
            if (tile.IsNode
                && middle.X >= tile.X && middle.X < tile.X + tile.Width
                && middle.Y >= tile.Y && middle.Y < tile.Y + tile.Height)
            {
                under.Add(tile);
            }
        }

        for (var at = under.Count - 1; at >= 0; at--)
        {
            // Placed in the whole picture rather than read off the drawing, which holds only what
            // reaches the screen: a shape a reordering sent several screens away is not in it.
            if (earlier.Locate(under[at].Node) is not { } from
                || onward(under[at].Node) is not { } now
                || later.Locate(now) is not { } to)
            {
                continue;
            }

            // Where the middle was inside the shape, as a share of it, put at the same share of where
            // the shape is now. A shape that grew keeps the reader on the same part of it.
            var x = to.X + ((0.5 - from.X) / from.Width * to.Width);
            var y = to.Y + ((0.5 - from.Y) / from.Height * to.Height);

            // Both drawings fill the screen, so a share of either is a share of the screen: the
            // picture is dragged by however far that point moved, the other way.
            return viewport.Panned(0.5 - x, 0.5 - y);
        }

        return viewport;
    }
}
