namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The levels of detail a treemap at one zoom is laid out at, and how a folder is framed at each.
///
/// <para>A level for the whole picture's scale, and one more for each doubling of the zoom, each
/// with the limits in the magnified picture's pixels. This is what lets a zoom show more without
/// moving what it already showed. Every folder, and every run of files too small to draw, is laid
/// out once, at the first level where it fits, and from then on it is only magnified: its band, its
/// gap and the rows inside it are the same shapes at every zoom past that level, scaled with the
/// picture. So a shape on screen when a zoom begins is exactly where the magnified picture put it
/// when the zoom ends, and what the zoom adds is only what did not fit before: a folder that opens,
/// or a run of small files drawn one by one. Limits that stayed in canvas pixels instead would lay
/// every folder out afresh at every zoom, and its contents would shuffle under the pointer as the
/// zoom settled.</para>
///
/// <para>The depth limit is part of the same rule. Past it a whole volume is frames round frames,
/// paid for on every repaint, and neither half of that holds for a magnified part of one: only what
/// is on the canvas is laid out, and the frames a level further down are the ones the zoom has just
/// made big enough to read. So each level of detail allows one more.</para>
///
/// <para>Separate from <see cref="TreemapLayout"/>, which packs rows into whatever space it is given
/// and holds no opinion on which limits a space is held to (G1).</para>
/// </summary>
internal sealed class TreemapDetail
{
    private readonly Level[] _levels;

    public TreemapDetail(LayoutLimits limits, MapViewport viewport)
    {
        _levels = new Level[(int)Math.Floor(Math.Log2(viewport.Zoom)) + 1];

        for (var level = 0; level < _levels.Length; level++)
        {
            var pixel = viewport.Zoom / Math.Pow(2, level);

            _levels[level] = new Level(
                limits.At(pixel) with { MaximumDepth = limits.MaximumDepth + level },
                pixel);
        }
    }

    /// <summary>How many levels this zoom has reached: one at the whole picture.</summary>
    public int Count => _levels.Length;

    /// <summary>The limits a space laid out at <paramref name="level"/> is held to.</summary>
    public LayoutLimits this[int level] => _levels[level].Limits;

    /// <summary>
    /// The first level, from <paramref name="from"/>, at which <paramref name="node"/> opens to show
    /// what it holds, drawn at <paramref name="width"/> by <paramref name="height"/>; or -1 where it
    /// stays one block at this zoom.
    ///
    /// <para>From the level of whatever placed it, because a folder cannot open before the rows it
    /// sits in were laid; and the first level that will do rather than the zoom's own, because that is
    /// what keeps the answer the same at every zoom that reaches it.</para>
    /// </summary>
    public int OpeningLevel(ISizedTree tree, int node, int depth, double width, double height, int from)
    {
        if (!tree.IsContainer(node))
        {
            return -1;
        }

        for (var level = from; level < _levels.Length; level++)
        {
            var limits = _levels[level].Limits;

            if (depth >= limits.MaximumDepth)
            {
                continue;
            }

            var (header, gap) = FrameOf(width, height, level);
            var top = header > 0 ? header : gap;

            if (width - (gap * 2) >= limits.MinimumTileSize
                && height - top - gap >= limits.MinimumTileSize)
            {
                return level;
            }
        }

        return -1;
    }

    /// <summary>
    /// The frame a folder opened at <paramref name="level"/> keeps round what it holds: a band along
    /// its top for its name, and a gap down its sides and along its bottom. Where there is no room for
    /// the band, a gap on all four sides; where there is no room for that, a single pixel; and in a
    /// rectangle too small for even that, nothing. Two pixels of frame inside a six-pixel tile leaves
    /// nothing to draw the children in.
    ///
    /// <para>The band is given only where the name fits across it and the children keep at least as
    /// much height again below it. Less than that, and the folder would be all name and no
    /// contents.</para>
    ///
    /// <para>The single pixel is a pixel of the level, not of the canvas, as every limit is. Left as a
    /// canvas pixel it was the one measure of the frame that did not scale, so a folder framed by it
    /// was framed more thinly the further the picture was zoomed, and what it held grew and moved.</para>
    ///
    /// <para>The frame is not free, and the cost is a real distortion rather than lost pixels.
    /// Barlow and Neville (Proc. IEEE InfoVis 2001) put it exactly: with an offset, a rectangle's
    /// area is proportional to its size <em>relative to all its ancestors</em>, so two equal nodes
    /// at different depths get different areas, and a band of text per level makes that larger than
    /// a one-pixel border does. It is the cost every treemap that names its folders in place
    /// accepts, Space Monger's among them, because the alternative is a picture whose folders cannot
    /// be told apart. The spacing setting is how a reader trades one for the other. Lü and Fogarty's
    /// two-stage layout (Graphics Interface 2008) is the correction, and it is a different
    /// algorithm.</para>
    /// </summary>
    public (float Header, float Gap) FrameOf(double width, double height, int level)
    {
        var (limits, pixel) = _levels[level];
        var gap = limits.ContainerGap;

        if (width >= limits.MinimumLabelWidth && height >= (limits.HeaderHeight * 2) + gap)
        {
            return (limits.HeaderHeight, gap);
        }

        var shorter = Math.Min(width, height);
        var smallest = limits.MinimumTileSize * 4;

        if (shorter >= smallest + (gap * 2))
        {
            return (0, gap);
        }

        return shorter >= smallest ? (0, Math.Min(gap, (float)pixel)) : (0, 0);
    }

    /// <summary>One level: its limits, and how wide one of its pixels is in the magnified picture.</summary>
    private readonly record struct Level(LayoutLimits Limits, double Pixel);
}
