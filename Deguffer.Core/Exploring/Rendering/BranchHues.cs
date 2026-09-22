using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The part of the hue circle one node owns, and whether it is drawn a step lighter than the
/// siblings either side of it.
/// </summary>
/// <param name="From">Where the node's arc starts, in degrees.</param>
/// <param name="Sweep">How wide the arc is, in degrees.</param>
/// <param name="Lifted">
/// Whether the node is drawn a step lighter. Alternate siblings take it, so two neighbours whose hues
/// are close still differ in lightness, which is the difference a colour-vision deficiency keeps.
/// </param>
public readonly record struct BranchHue(double From, double Sweep, bool Lifted)
{
    /// <summary>The whole circle, which is what the node a drawing is rooted at owns.</summary>
    public static BranchHue Whole { get; } = new(0, 360, false);

    /// <summary>The hue the node itself is painted in: the middle of its arc.</summary>
    public double Centre => From + (Sweep / 2);
}

/// <summary>
/// Which part of the hue circle each node of one drawing owns: Tennekes and de Jonge, <i>Tree
/// Colors: Color Schemes for Tree-Structured Data</i>, IEEE TVCG 20(12), 2014.
///
/// <para>The node the drawing is rooted at owns the whole circle. Each node divides its own arc
/// among its children and keeps a gap between them, so every descendant of a top-level folder has a
/// hue inside that folder's arc. A reader who knows one folder's colour then knows the colour of
/// everything in it, however deep, and two folders side by side differ by as much of the circle as
/// their parent could give them.</para>
///
/// <para>The children take their arcs in an interleaved order rather than in size order, as the
/// paper recommends: the largest and the second largest are then about half the parent's arc
/// apart, rather than neighbours on the circle as well as on the screen.</para>
///
/// <para>Worked out as nodes are asked for and remembered for the life of the drawing, and a node's
/// siblings are given their arcs in the same pass as the node, because a folder's children are
/// drawn together (G4).</para>
/// </summary>
internal sealed class BranchHues(ISizedTree tree, int root)
{
    /// <summary>
    /// How much of its share each child keeps, per the paper. The quarter left over is the gap that
    /// keeps a child's own children from reaching the hue of its neighbour's.
    /// </summary>
    private const double Kept = 0.75;

    private readonly Dictionary<int, BranchHue> _hues = [];

    private readonly Stack<int> _unresolved = new();

    /// <summary>The arc <paramref name="node"/> owns in this drawing.</summary>
    public BranchHue Of(int node)
    {
        // Up to the nearest node whose arc is already known, remembering the way. Iterative for the
        // reason the layouts are: an icicle can draw a very deep tree.
        var current = node;

        while (!IsRoot(current) && !_hues.ContainsKey(current))
        {
            _unresolved.Push(current);
            current = tree.ParentOf(current);
        }

        while (_unresolved.TryPop(out var next))
        {
            var parent = tree.ParentOf(next);

            Divide(parent, IsRoot(parent) ? BranchHue.Whole : _hues[parent]);
        }

        return IsRoot(node) ? BranchHue.Whole : _hues[node];
    }

    /// <summary>
    /// Whether <paramref name="node"/> takes the whole circle: the drawing's root, or the tree's own
    /// root, which is reached only by a node that is not under the drawing's root at all.
    /// </summary>
    private bool IsRoot(int node) => node == root || node == tree.RootNode;

    /// <summary>Give each of <paramref name="parent"/>'s children its arc of <paramref name="arc"/>.</summary>
    private void Divide(int parent, BranchHue arc)
    {
        var children = tree.ChildrenOf(parent);
        var share = arc.Sweep / children.Length;

        for (var i = 0; i < children.Length; i++)
        {
            var slot = Slot(i, children.Length);

            _hues[children[i]] = new BranchHue(
                arc.From + (slot * share) + (share * (1 - Kept) / 2),
                share * Kept,
                i % 2 == 1);
        }
    }

    /// <summary>
    /// Where the child at <paramref name="index"/> sits among <paramref name="count"/>: the even
    /// positions fill the first half of the arc and the odd ones the second, so children adjacent in
    /// size order are about half the arc apart.
    /// </summary>
    private static int Slot(int index, int count) =>
        index % 2 == 0 ? index / 2 : ((count + 1) / 2) + (index / 2);
}
