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
    /// <paramref name="branchOf"/> says which top-level branch a node belongs to, which is what
    /// gives a whole subtree one hue. It is supplied rather than derived because the tree does not
    /// know which node the view is currently rooted at.
    /// </summary>
    public static void Paint(
        byte[] pixels,
        IReadOnlyList<ExploreTile> tiles,
        int width,
        int height,
        TileColour background,
        Func<int, int> branchOf)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(branchOf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (pixels.Length < PixelBuffer.LengthFor(width, height))
        {
            throw new ArgumentException(
                $"A {width}x{height} canvas needs {PixelBuffer.LengthFor(width, height)} bytes, not {pixels.Length}.",
                nameof(pixels));
        }

        PixelBuffer.Fill(pixels, background);

        foreach (var tile in tiles)
        {
            var colour = tile.IsAggregate
                ? TilePalette.Aggregate
                : TilePalette.For(branchOf(tile.Node), tile.Depth);

            Cushion(pixels, width, height, tile, colour);
        }
    }

    private static void Cushion(byte[] pixels, int width, int height, ExploreTile tile, TileColour colour)
    {
        var left = Math.Max(0, (int)MathF.Round(tile.X));
        var top = Math.Max(0, (int)MathF.Round(tile.Y));
        var right = Math.Min(width, (int)MathF.Round(tile.X + tile.Width));
        var bottom = Math.Min(height, (int)MathF.Round(tile.Y + tile.Height));

        if (right <= left || bottom <= top)
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

        void Row(int y)
        {
            var v = spanHeight <= 1 ? 0.5 : (double)(y - top) / (spanHeight - 1);
            var ny = ridge * ((2 * v) - 1);

            for (var x = left; x < right; x++)
            {
                var u = spanWidth <= 1 ? 0.5 : (double)(x - left) / (spanWidth - 1);
                var nx = ridge * ((2 * u) - 1);

                CushionShading.Write(
                    pixels, ((y * width) + x) * 4, colour, CushionShading.LightAt(nx, ny));
            }
        }

        PixelBuffer.Rows(top, bottom, spanWidth * spanHeight, Row);
    }
}
