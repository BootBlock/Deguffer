using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One rectangle a layout produced, in the canvas coordinates it was asked for.
///
/// <para>Deliberately not a drawing instruction. It carries where and how big, and the view decides
/// what colour it is and whether it gets a label — which is what keeps the layout algorithms in
/// Core, where they can be tested, rather than inside a XAML control where they cannot (G1).</para>
/// </summary>
/// <param name="Node">
/// The node in the tree, <see cref="Aggregated"/> where this rectangle stands for several siblings
/// too small to draw individually, <see cref="FreeSpace"/> where it stands for what the volume has
/// left, or <see cref="Unaccounted"/> where it stands for what the volume has in use that the scan
/// did not count.
/// </param>
/// <param name="Depth">How far below the layout's root this sits. The root itself is zero.</param>
/// <param name="Bytes">
/// What this rectangle represents. Carried rather than looked up because an aggregate has no node
/// to look it up from, and the whole reason to draw an aggregate rather than a gap is to be able to
/// say how much is hidden in it.
/// </param>
/// <param name="Header">
/// How tall the band along the top of a folder's frame is, which is where its name goes, or zero
/// where the layout gave it no band. Carried rather than worked out again from the size, because
/// the layout decided it and the label and the shading have to agree with that decision.
/// </param>
public readonly record struct ExploreTile(
    int Node,
    int Depth,
    long Bytes,
    float X,
    float Y,
    float Width,
    float Height,
    float Header = 0)
{
    /// <summary>
    /// The node number of a rectangle standing in for omitted siblings.
    ///
    /// <para>Every disk tool has to solve the same problem — a volume holds millions of files and a
    /// screen holds a million pixels — and the three answers in use are to stop recursing, to cull
    /// below a threshold, or to aggregate. Aggregating is the one that does not lie: the remainder
    /// keeps the parent's proportions and states its own byte count, so what is hidden is visible
    /// as a quantity even though it is not visible as items.</para>
    /// </summary>
    public const int Aggregated = -1;

    /// <summary>
    /// The node number of the rectangle standing for the volume's free space, drawn beside the whole
    /// of a scanned volume so the used space is seen in proportion to what is left.
    ///
    /// <para>Not a thing on the disk, so it can never be picked, opened or outlined (§7.1). Every
    /// caller that acts on a shape asks <see cref="IsNode"/>, which this and both other blocks fail.</para>
    /// </summary>
    public const int FreeSpace = -2;

    /// <summary>
    /// The node number of the rectangle standing for what the volume has in use and the scan did not
    /// count: folders it could not read, and the file system's own records. Drawn beside the free
    /// space, because without it the free space would take the share of the picture these bytes
    /// belong to, and a lower bound would look like the whole drive (§7.1).
    ///
    /// <para>Not a thing on the disk either, on the terms <see cref="FreeSpace"/> is.</para>
    /// </summary>
    public const int Unaccounted = -3;

    public bool IsAggregate => Node == Aggregated;

    public bool IsFreeSpace => Node == FreeSpace;

    public bool IsUnaccounted => Node == Unaccounted;

    /// <summary>Whether this rectangle is a node of the tree, rather than one standing for something else.</summary>
    public bool IsNode => Node >= 0;

    /// <summary>
    /// Whether this rectangle has room for a readable label.
    ///
    /// <para>Here rather than on the rasteriser, which draws no text and says so. The thresholds are
    /// pixel limits like every other field of <see cref="LayoutLimits"/>, and they have to be scaled
    /// for the display the same way — a 48-pixel floor is 48 device pixels at 100% and 96 at 200%,
    /// so comparing raw constants against a layout measured in device pixels labels rectangles half
    /// the intended size on a high-DPI screen.</para>
    ///
    /// <para>It decides how many controls the shell creates as much as what is readable: without it
    /// a full volume wants fifty thousand text blocks, and with it a few dozen.</para>
    /// </summary>
    public bool HasRoomForALabel(LayoutLimits limits) =>
        Width >= limits.MinimumLabelWidth && Height >= limits.MinimumLabelHeight;
}

/// <summary>
/// How much detail to draw. Both numbers are pixel thresholds rather than preferences, and both
/// come from what shipping tools settled on.
/// </summary>
/// <param name="MinimumTileSize">
/// The smallest rectangle worth drawing, in device-independent pixels. QDirStat's default is 3, and
/// its stated reason is as much about interaction as speed — below it the user cannot tell what
/// they are about to click on. Scale this by the display's DPI: a 3-pixel floor at 100% is a
/// pixel and a half at 200%.
/// </param>
/// <param name="MaximumDepth">
/// How many levels a nesting layout descends. WinDirStat's default is 6. Past that the rectangles
/// are frames around frames, and the cost is paid on every repaint.
///
/// <para>This governs the treemap and the sunburst, which are both bounded in every direction at
/// once. It does not govern the icicle, whose levels are rows rather than nested frames: there the
/// canvas runs out first, and a depth cap only leaves the panel blank.</para>
///
/// <para>It is the depth of the whole picture. A zoomed treemap goes one level further for each
/// doubling of the zoom; see <see cref="TreemapDetail"/>.</para>
/// </param>
/// <param name="RowHeight">
/// The smallest band a level can be drawn in, in device-independent pixels. It is exactly the
/// icicle's row height, and it is the floor under a sunburst ring's width — that layout divides the
/// radius among its rings, and this is what makes a small window show fewer levels rather than
/// unreadable ones.
///
/// <para>Scaled like the other thresholds: a 22-pixel row at 100% is eleven device pixels at 200%
/// unless it is scaled, which halves the number of levels on screen and leaves the text taller than
/// the band it belongs to.</para>
/// </param>
/// <param name="MinimumLabelWidth">
/// The narrowest rectangle worth putting text in. WinDirStat draws a treemap label at 16 by 16 and
/// a flame-graph label at 40 by 14; a name and a size together need more width than either.
/// </param>
/// <param name="MinimumLabelHeight">The shortest rectangle worth putting text in.</param>
/// <param name="ContainerGap">
/// The gap a treemap leaves between a folder's edge and what it holds, down its sides and along its
/// bottom. The top edge has <see cref="HeaderHeight"/> instead. See <see cref="ExploreSpacing"/>.
/// </param>
public readonly record struct LayoutLimits(
    float MinimumTileSize,
    int MaximumDepth,
    float RowHeight,
    float MinimumLabelWidth,
    float MinimumLabelHeight,
    float ContainerGap)
{
    public static readonly LayoutLimits Default = new(
        MinimumTileSize: 3f,
        MaximumDepth: 6,
        RowHeight: 22f,
        MinimumLabelWidth: 48f,
        MinimumLabelHeight: 16f,
        ContainerGap: 3f);

    /// <summary>
    /// The band along the top of a folder's frame: one line of text, with half the gap above it and
    /// half below, so a wider spacing loosens the band as much as the sides.
    /// </summary>
    public float HeaderHeight => MinimumLabelHeight + ContainerGap;

    /// <summary>These limits with the gap <paramref name="spacing"/> asks for, in device-independent pixels.</summary>
    public LayoutLimits Spaced(ExploreSpacing spacing) => this with
    {
        ContainerGap = spacing switch
        {
            ExploreSpacing.Dense => 1f,
            ExploreSpacing.Spacious => 6f,
            _ => 3f,
        },
    };

    /// <summary>
    /// The gap between a shape's edge and the text inside it. A quarter of the label height, so the
    /// padding stays in proportion to the text it separates instead of being a second constant that
    /// has to be remembered and scaled.
    /// </summary>
    public float LabelPadding => MinimumLabelHeight / 4;

    /// <summary>
    /// These limits with the text thresholds grown by <paramref name="textScale"/>, the reader's
    /// Windows text size.
    ///
    /// <para>The labels are text controls and grow with that setting, as they have to. A threshold
    /// left at its 100% size then gives a band one line of 100% text tall to a line of 150% text,
    /// and the name spills out of the band onto the shapes below it, where its colour was not
    /// chosen for what it sits on (§6.5).</para>
    /// </summary>
    public LayoutLimits ForText(double textScale) => this with
    {
        MinimumLabelWidth = (float)(MinimumLabelWidth * textScale),
        MinimumLabelHeight = (float)(MinimumLabelHeight * textScale),
    };

    /// <summary>The same limits in device pixels, for a display at <paramref name="scale"/>.</summary>
    public LayoutLimits At(double scale) => new(
        (float)(MinimumTileSize * scale),
        MaximumDepth,
        (float)(RowHeight * scale),
        (float)(MinimumLabelWidth * scale),
        (float)(MinimumLabelHeight * scale),
        (float)(ContainerGap * scale));
}
