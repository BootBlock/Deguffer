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
    /// <para>Tiles are walked from the end of the list towards the start, and the first shape to
    /// claim a pixel keeps it. That is the same picture as painting them in the order given, where
    /// a later shape covers an earlier one: whichever of two overlapping shapes comes later wins
    /// under both rules. What it avoids is shading a pixel once for every level above it — see
    /// <see cref="ClaimedPixels"/> for what that costs on a real volume.</para>
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

        // Indexed as an array below rather than through the interface. Every band walks the whole
        // list, so on a 4K canvas of thirty thousand rectangles that is a couple of million calls
        // through an interface indexer returning a 32-byte struct, per repaint (G4). The layouts
        // hand back arrays, so the copy is the fallback rather than the usual case.
        var shapes = tiles as ExploreTile[] ?? [.. tiles];

        // Split by rows, not by rectangle. A treemap of a real volume is tens of thousands of small
        // rectangles and a handful of large ones, so a partition drawn around each rectangle in
        // turn leaves almost every one of them below any threshold worth handing to a second
        // thread — and the canvas is shaded on one core while the rest sit idle (G4).
        //
        // A band owns its rows outright, and every rectangle is offered to every band, clipped to
        // the rows that band holds. So the picture is the one a single thread would have produced.
        PixelBuffer.Bands(0, height, width * height, (from, to) =>
        {
            PixelBuffer.Fill(pixels, width, from, to, background);

            var claimed = new ClaimedPixels(width, from, to);

            // Backwards, and stopping the moment the band is entirely spoken for. Both are the
            // same argument: a shape can only show where nothing nested inside it already does.
            for (var i = shapes.Length - 1; i >= 0 && !claimed.IsFull; i--)
            {
                Cushion(pixels, claimed, width, height, shapes[i], colours[i]);
            }
        });
    }

    /// <summary>
    /// Shade one rectangle into whichever of <paramref name="claimed"/>'s rows and pixels are still
    /// free.
    ///
    /// <para>The cushion is measured across the whole rectangle and only <em>drawn</em> where it
    /// shows. Measuring it within the band instead would restart the gradient at every band
    /// boundary and put a seam across the picture wherever one fell; measuring it across only the
    /// unclaimed part would stretch a shape's whole cushion into the sliver of it that is
    /// visible.</para>
    /// </summary>
    private static void Cushion(
        byte[] pixels,
        ClaimedPixels claimed,
        int width,
        int height,
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

        var firstRow = Math.Max(top, claimed.Top);
        var lastRow = Math.Min(bottom, claimed.Bottom);

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
                if (claimed.Claim(x, y))
                {
                    var u = spanWidth <= 1 ? 0.5 : (double)(x - left) / (spanWidth - 1);
                    var nx = ridge * ((2 * u) - 1);

                    CushionShading.Write(pixels, offset, colour, CushionShading.LightAt(nx, ny));
                }

                offset += 4;
            }
        }
    }
}
