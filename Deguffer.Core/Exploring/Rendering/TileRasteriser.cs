using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Draws laid-out rectangles into a pixel buffer.
///
/// <para>A bitmap rather than one shaped element per rectangle, because a full volume lays out to
/// tens of thousands of them and the framework's own guidance is that a vector element repeated
/// enough times should become an image instead. It is also what the reference implementations do —
/// WinDirStat renders into a top-down DIB once and blits it, drawing only the selection live over
/// the cached frame.</para>
///
/// <para>In Core rather than in the shell because it is a pure function from rectangles to bytes,
/// with no window, no dispatcher and no theme object anywhere in it. The shell has no test project,
/// so anything left there is verifiable only by looking at it (G8).</para>
///
/// <para>Text is deliberately not drawn here. Labels are laid over the finished bitmap as real
/// controls, which keeps them selectable, scalable with the user's text size, and visible to a
/// screen reader — none of which a label burnt into a bitmap is.</para>
/// </summary>
public static class TileRasteriser
{
    /// <summary>
    /// Paint <paramref name="tiles"/> into <paramref name="pixels"/>, a BGRA buffer of
    /// <paramref name="width"/> × <paramref name="height"/>.
    ///
    /// <para>The buffer belongs to the caller and is overwritten in full, so a view that repaints
    /// keeps one and hands it back (G5). At 3840 by 2160 it is 33 MB — every one of them a
    /// large-object-heap allocation, and the heap is not compacted by default — so allocating per
    /// repaint would leak tens of megabytes a second for the length of a scan.</para>
    ///
    /// <para>Tiles are painted in the order given, which the layouts produce parent-first — so a
    /// child covers its parent and the nesting comes out right without sorting anything here.</para>
    ///
    /// <paramref name="colourOf"/> answers what one shape is painted, given its node and its
    /// depth. Supplied rather than decided here because what a colour means is the surface's choice
    /// — a hue per branch, or a band per age — and because the tree does not know which node the
    /// view is currently rooted at, which is what a branch is measured from.
    /// </summary>
    public static void Paint(
        byte[] pixels,
        IReadOnlyList<ExploreTile> tiles,
        int width,
        int height,
        TileColour background,
        Func<int, int, TileColour> colourOf)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(colourOf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (pixels.Length < PixelBuffer.LengthFor(width, height))
        {
            throw new ArgumentException(
                $"A {width}x{height} canvas needs {PixelBuffer.LengthFor(width, height)} bytes, not {pixels.Length}.",
                nameof(pixels));
        }

        // One colour per rectangle, before a single pixel is written, as SectorRasteriser does. The
        // reason is sharper here: each band below walks the whole list, so resolving a colour inside
        // that loop would climb a node's ancestors once per band as well as once per rectangle (G4).
        var colours = new TileColour[tiles.Count];

        for (var i = 0; i < tiles.Count; i++)
        {
            colours[i] = colourOf(tiles[i].Node, tiles[i].Depth);
        }

        // Split by rows, not by rectangle. Handing each rectangle to Parallel.For in turn — which is
        // what painting one shape at a time amounted to — only ever cleared the threshold for the
        // handful of large ones, so a canvas made of fifty thousand small rectangles was shaded on a
        // single thread while every other core sat idle. It also built a closure per rectangle.
        //
        // A band owns its rows outright, and every rectangle is offered to every band in the order
        // the layout gave, clipped to the rows that band holds. So the nesting still comes out of
        // the painting order and the picture is the one a single thread would have produced.
        PixelBuffer.Bands(0, height, width * height, (from, to) =>
        {
            PixelBuffer.Fill(pixels, width, from, to, background);

            for (var i = 0; i < tiles.Count; i++)
            {
                Cushion(pixels, width, height, from, to, tiles[i], colours[i]);
            }
        });
    }

    /// <summary>
    /// Shade one rectangle into the rows between <paramref name="bandTop"/> and
    /// <paramref name="bandBottom"/>.
    ///
    /// <para>The cushion is measured across the whole rectangle and only <em>drawn</em> within the
    /// band. Measuring it within the band instead would restart the gradient at every band boundary
    /// and put a seam across the picture wherever one fell.</para>
    /// </summary>
    private static void Cushion(
        byte[] pixels,
        int width,
        int height,
        int bandTop,
        int bandBottom,
        ExploreTile tile,
        TileColour colour)
    {
        var left = Math.Max(0, (int)MathF.Round(tile.X));
        var top = Math.Max(0, (int)MathF.Round(tile.Y));
        var right = Math.Min(width, (int)MathF.Round(tile.X + tile.Width));
        var bottom = Math.Min(height, (int)MathF.Round(tile.Y + tile.Height));

        if (right <= left || bottom <= top)
        {
            return;
        }

        var firstRow = Math.Max(top, bandTop);
        var lastRow = Math.Min(bottom, bandBottom);

        if (lastRow <= firstRow)
        {
            return;
        }

        var ridge = CushionShading.RidgeAt(tile.Depth);

        // An aggregate is not a thing on the disk, so it is drawn flat: a cushion would give it the
        // same physical presence as the files it stands in for.
        if (tile.IsAggregate)
        {
            ridge = 0;
        }

        var spanWidth = right - left;
        var spanHeight = bottom - top;

        for (var y = firstRow; y < lastRow; y++)
        {
            var v = spanHeight <= 1 ? 0.5 : (double)(y - top) / (spanHeight - 1);
            var ny = ridge * ((2 * v) - 1);
            var offset = ((y * width) + left) * 4;

            for (var x = left; x < right; x++)
            {
                var u = spanWidth <= 1 ? 0.5 : (double)(x - left) / (spanWidth - 1);
                var nx = ridge * ((2 * u) - 1);

                CushionShading.Write(pixels, offset, colour, CushionShading.LightAt(nx, ny));
                offset += 4;
            }
        }
    }
}
