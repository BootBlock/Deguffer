using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>What the pointer found: a node, or the block standing in for items too small to draw.</summary>
/// <param name="Bytes">
/// What was pointed at accounts for this much. Carried rather than looked up because an aggregate
/// has no node to look it up from.
/// </param>
public readonly record struct ExploreHit(int Node, long Bytes)
{
    public bool IsAggregate => Node == ExploreTile.Aggregated;
}

/// <summary>
/// Where a view should put one piece of text, in canvas pixels.
///
/// <para>Positions rather than drawn text, because the labels are the one part of the picture that
/// is not in the bitmap: they are real controls laid over it, so they scale with the user's text
/// size and a screen reader can reach them. What belongs here is the part that is geometry, and
/// what belongs to the shell is the part that is a control.</para>
/// </summary>
/// <param name="X">The left edge of a box one line high. The text is trimmed to fit it.</param>
/// <param name="Y">The top of that box.</param>
/// <param name="Rotation">
/// How far to turn the text about the middle of its box, in degrees clockwise. Zero for anything
/// laid out in rectangles; a sunburst turns each label to lie along its own ring.
/// </param>
/// <param name="Centred">Whether the text sits in the middle of its box or starts at the left of it.</param>
/// <param name="Colour">
/// What colour the text has to be to stay legible against the shape underneath it. Decided here
/// because the surface is what knows the colour it painted that shape in.
/// </param>
public readonly record struct ExploreLabel(
    int Node,
    float X,
    float Y,
    float Width,
    float Rotation,
    bool Centred,
    TileColour Colour);

/// <summary>
/// One drawing of one node of one tree: its geometry, what is under a given point, where the text
/// goes, and how to paint it.
///
/// <para>These four answers vary together and only together — a sunburst is laid out, pointed at,
/// labelled and painted differently from a treemap in one consistent set — so they are one type per
/// drawing rather than four parallel switches in the view (G1). A fifth way of drawing a volume is
/// then a new subclass, not an edit to any of them.</para>
///
/// <para>In Core rather than in the shell for the usual reason: none of it needs a window, and the
/// shell has no test project (G8).</para>
/// </summary>
public abstract class ExploreSurface
{
    /// <summary>
    /// How many labels to draw at most.
    ///
    /// <para>The size threshold already keeps this small on an ordinary tree, but a directory of
    /// several hundred near-equal children defeats it — every shape is then big enough to label and
    /// none of them is interesting. Past a few dozen the labels are noise over the picture anyway,
    /// and the list view is the honest way to read that many names.</para>
    /// </summary>
    protected const int MaximumLabels = 64;

    /// <summary>
    /// Which of the root's children each direct child is, so a whole subtree shares one hue.
    ///
    /// <para>The answer is the branch's <em>position</em> among its siblings, not its node number.
    /// Node numbers are whatever the scan happened to assign, so taking them modulo the palette
    /// gives two adjacent branches the same hue often enough to be visible — and the two largest
    /// shapes on the screen sharing a colour is precisely the collision that matters.</para>
    /// </summary>
    private readonly Dictionary<int, int> _branches = [];

    protected ExploreSurface(ExploreTree tree, int root, int width, int height, LayoutLimits limits)
    {
        ArgumentNullException.ThrowIfNull(tree);

        Tree = tree;
        Root = root;
        Width = width;
        Height = height;
        Limits = limits;

        var children = tree.ChildrenOf(root);

        for (var i = 0; i < children.Length; i++)
        {
            _branches[children[i]] = i;
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Where the text goes, at most <see cref="MaximumLabels"/> of them.</summary>
    public abstract IReadOnlyList<ExploreLabel> Labels { get; }

    protected ExploreTree Tree { get; }

    /// <summary>The node being drawn, which is the whole canvas rather than the tree's own root.</summary>
    protected int Root { get; }

    protected LayoutLimits Limits { get; }

    /// <summary>
    /// Lay <paramref name="root"/> of <paramref name="tree"/> out for <paramref name="view"/>, on a
    /// canvas of <paramref name="width"/> by <paramref name="height"/> device pixels at
    /// <paramref name="scale"/>.
    ///
    /// <para>The scale is applied to the thresholds here rather than by the caller, because every
    /// one of them is stated in device-independent pixels and a layout measured in device pixels
    /// compared against raw constants draws half-size detail on a high-DPI display.</para>
    /// </summary>
    public static ExploreSurface Create(
        ExploreTree tree, int root, ExploreView view, int width, int height, double scale)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var limits = LayoutLimits.Default.At(scale);

        return view switch
        {
            ExploreView.Sunburst => new SunburstSurface(tree, root, width, height, limits),
            ExploreView.Icicle => new TiledSurface(
                tree, root, width, height, limits,
                IcicleLayout.Compute(tree, root, width, height, limits)),

            // Including List, which draws no map at all. The page hides the map rather than telling
            // it to stop, so this is the drawing it will be showing again when the user switches
            // back, and it is the one they last saw.
            _ => new TiledSurface(
                tree, root, width, height, limits,
                TreemapLayout.Compute(tree, root, width, height, limits)),
        };
    }

    /// <summary>Paint the whole canvas into <paramref name="pixels"/>, a BGRA buffer of that size.</summary>
    public abstract void Paint(byte[] pixels, TileColour background);

    /// <summary>What is at this canvas point, or null where the point is over nothing.</summary>
    public abstract ExploreHit? At(float x, float y);

    /// <summary>
    /// Which top-level branch a node belongs to, so a whole subtree shares one hue.
    ///
    /// <para>Walked up from the node rather than carried in the shape, because "top level" means
    /// relative to whatever the user has descended into — the same node is a branch of its own when
    /// opened, and part of a larger one when seen from above.</para>
    /// </summary>
    protected int BranchOf(int node)
    {
        var current = node;

        while (current != Root && Tree.ParentOf(current) != Root && current != Tree.RootNode)
        {
            current = Tree.ParentOf(current);
        }

        return _branches.TryGetValue(current, out var position) ? position : 0;
    }

    /// <summary>The colour text over a shape of this node at this depth has to be drawn in.</summary>
    protected TileColour TextColourFor(int node, int depth) =>
        TilePalette.For(BranchOf(node), depth).ContrastingText;
}
