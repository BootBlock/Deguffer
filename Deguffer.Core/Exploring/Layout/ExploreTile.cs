namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One rectangle a layout produced, in the canvas coordinates it was asked for.
///
/// <para>Deliberately not a drawing instruction. It carries where and how big, and the view decides
/// what colour it is and whether it gets a label — which is what keeps the layout algorithms in
/// Core, where they can be tested, rather than inside a XAML control where they cannot (G1).</para>
/// </summary>
/// <param name="Node">
/// The node in the tree, or <see cref="Aggregated"/> where this rectangle stands for several
/// siblings too small to draw individually.
/// </param>
/// <param name="Depth">How far below the layout's root this sits. The root itself is zero.</param>
/// <param name="Bytes">
/// What this rectangle represents. Carried rather than looked up because an aggregate has no node
/// to look it up from, and the whole reason to draw an aggregate rather than a gap is to be able to
/// say how much is hidden in it.
/// </param>
public readonly record struct ExploreTile(
    int Node,
    int Depth,
    long Bytes,
    float X,
    float Y,
    float Width,
    float Height)
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

    public bool IsAggregate => Node == Aggregated;
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
/// How many levels to descend. WinDirStat's default is 6. Past that the rectangles are frames
/// around frames, and the cost is paid on every repaint.
/// </param>
public readonly record struct LayoutLimits(float MinimumTileSize, int MaximumDepth)
{
    public static readonly LayoutLimits Default = new(MinimumTileSize: 3f, MaximumDepth: 6);
}
