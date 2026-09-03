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
    /// Cushion parameters, from van Wijk and van de Wetering, <i>Cushion Treemaps</i>, Proc. IEEE
    /// InfoVis '99, pp. 73–78, at the values WinDirStat and KDirStat both settled on.
    ///
    /// <para>The shading is what makes nesting legible without a border around every rectangle, and
    /// this is the single-ridge form: each rectangle is shaded from its own bounds with the ridge
    /// height scaled down by depth, rather than accumulating its ancestors' surfaces into it. The
    /// difference is visible only where a deep tile sits on a steep part of its parent's cushion,
    /// and the accumulating form needs the whole ancestor chain carried through the layout.</para>
    /// </summary>
    private const double RidgeHeight = 0.38;

    private const double DepthScale = 0.91;
    private const double Ambient = 0.13;
    private const double Diffuse = 0.87;

    // Van Wijk's light, l = [1, 2, 10] normalised, with the two lateral components negated because
    // a bitmap's y axis runs downwards and the paper's does not. Negating both puts the key light
    // above and to the left, which is where every implementation of this puts it and where a reader
    // expects a highlight to be.
    //
    // The model is ambient plus Lambertian diffuse, and nothing else. There is no specular term:
    // the paper is explicit that "a simple model, i.e. diffuse reflection, suffices", and a
    // highlight would read as a material rather than as a shape.
    private const double LightX = -0.09759;
    private const double LightY = -0.19518;
    private const double LightZ = 0.9759;

    /// <summary>
    /// Rows big enough to be worth splitting across threads. Below this the scheduling costs more
    /// than the shading (G4).
    /// </summary>
    private const int ParallelPixelThreshold = 512 * 1024;

    /// <summary>
    /// G4: bounded explicitly rather than left to the pool. This is not a rare branch — the root
    /// rectangle of any map on a display wider than 1024 by 512 clears the threshold on every
    /// repaint, and a repaint happens per progress snapshot, per view change, per descend and
    /// continuously while a window edge is dragged. Processor count rather than twice it, because
    /// shading is arithmetic and waits on nothing.
    /// </summary>
    private static readonly ParallelOptions RowOptions = new()
    {
        MaxDegreeOfParallelism = Environment.ProcessorCount,
    };

    /// <summary>How large a buffer <see cref="Paint"/> needs for a canvas of this size.</summary>
    public static int BufferLengthFor(int width, int height) => width * height * 4;

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

        if (pixels.Length < BufferLengthFor(width, height))
        {
            throw new ArgumentException(
                $"A {width}x{height} canvas needs {BufferLengthFor(width, height)} bytes, not {pixels.Length}.",
                nameof(pixels));
        }

        Fill(pixels, background);

        foreach (var tile in tiles)
        {
            var colour = tile.IsAggregate
                ? TilePalette.Aggregate
                : TilePalette.For(branchOf(tile.Node), tile.Depth);

            Cushion(pixels, width, height, tile, colour);
        }
    }

    private static void Fill(byte[] pixels, TileColour colour)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = colour.Blue;
            pixels[i + 1] = colour.Green;
            pixels[i + 2] = colour.Red;
            pixels[i + 3] = 255;
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

        // The ridge flattens with depth, so a nested rectangle reads as sitting on its parent
        // rather than competing with it.
        var ridge = 4 * RidgeHeight * Math.Pow(DepthScale, tile.Depth);

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

                var cosine = ((nx * LightX) + (ny * LightY) + LightZ)
                    / Math.Sqrt((nx * nx) + (ny * ny) + 1);

                var light = Ambient + (Diffuse * Math.Clamp(cosine, 0, 1));
                var offset = ((y * width) + x) * 4;

                pixels[offset] = Shade(colour.Blue, light);
                pixels[offset + 1] = Shade(colour.Green, light);
                pixels[offset + 2] = Shade(colour.Red, light);
                pixels[offset + 3] = 255;
            }
        }

        if (spanWidth * spanHeight >= ParallelPixelThreshold)
        {
            Parallel.For(top, bottom, RowOptions, Row);
            return;
        }

        for (var y = top; y < bottom; y++)
        {
            Row(y);
        }
    }

    private static byte Shade(byte channel, double light) => (byte)Math.Clamp(channel * light, 0, 255);
}
