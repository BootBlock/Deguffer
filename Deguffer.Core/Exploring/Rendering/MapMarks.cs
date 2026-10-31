using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// What the map marks out, in node numbers, and which of it each mark is drawn round: what the user
/// picked, what a removal under way is acting on, the shape under the pointer and the shape the
/// keyboard is on.
///
/// <para>A shape carries one outline, the strongest claim on it. What is picked is outlined as picked,
/// so the pointer over it outlines nothing more, and the keyboard's shape is outlined only where it is
/// neither picked nor under the pointer, which mark it already. A block that stands for something
/// other than a node cannot be picked, so the pointer outlines none (§7.1).</para>
///
/// <para>The hatch over what is being removed is drawn only on a drawing of the tree its nodes are
/// numbers in: in any other tree the same numbers are other shapes (§7.1).</para>
///
/// <para>In Core rather than in the shell, as <see cref="MapPress"/> is, so the rules are provable
/// without a window (G8). Where the marks are drawn is the shell's.</para>
/// </summary>
public sealed class MapMarks
{
    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    /// <summary>What the user picked, as a set because it is asked of every shape in the drawing.</summary>
    private readonly HashSet<int> _picked = [];

    /// <summary>What a removal under way is acting on. See <see cref="MarkRemoving"/>.</summary>
    private readonly HashSet<int> _removing = [];

    /// <summary>The tree <see cref="_removing"/> names nodes of.</summary>
    private ISizedTree? _removingTree;

    /// <summary>
    /// The one node the pointer outlines, or none. Kept and rewritten rather than built per move: a
    /// pointer crosses a treemap's shapes many times a second, and each crossing would otherwise be an
    /// allocation (G5).
    /// </summary>
    private readonly HashSet<int> _under = [];

    /// <summary>What the user picked.</summary>
    public IReadOnlySet<int> Picked => _picked;

    /// <summary>
    /// Pick <paramref name="nodes"/> in place of what was picked. False where they are what was picked
    /// already, so nothing needs marking again.
    /// </summary>
    public bool Pick(IReadOnlyCollection<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (_picked.SetEquals(nodes))
        {
            return false;
        }

        _picked.Clear();
        _picked.UnionWith(nodes);

        return true;
    }

    /// <summary>
    /// Mark <paramref name="nodes"/> of <paramref name="tree"/> as what a removal under way is acting
    /// on, or nothing once it is over.
    /// </summary>
    public void MarkRemoving(ISizedTree? tree, IReadOnlyCollection<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        _removingTree = tree;
        _removing.Clear();
        _removing.UnionWith(nodes);
    }

    /// <summary>What to hatch on a drawing of <paramref name="drawn"/>: nothing unless it is the tree the removal named.</summary>
    public IReadOnlySet<int> RemovingIn(ISizedTree drawn) =>
        ReferenceEquals(drawn, _removingTree) ? _removing : None;

    /// <summary>
    /// The node to outline with the pointer over <paramref name="hit"/>: its node, unless it is a block or
    /// is picked. Remembered for <see cref="OutlinesFocus"/>.
    /// </summary>
    public IReadOnlySet<int> Hover(ExploreHit? hit)
    {
        _under.Clear();

        if (hit is { IsNode: true } node && !_picked.Contains(node.Node))
        {
            _under.Add(node.Node);
        }

        return _under;
    }

    /// <summary>
    /// Whether the keyboard's shape, <paramref name="node"/>, gets an outline of its own: only where it
    /// is neither picked nor what the pointer outlines (<see cref="Hover"/>).
    /// </summary>
    public bool OutlinesFocus(int node) => !_picked.Contains(node) && !_under.Contains(node);
}
