using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Where each node's shapes sit in a drawing's list of them, so a drawing asked about a few nodes
/// reads those nodes' shapes and no others.
///
/// <para>Hovering asks a drawing for the shape of the node under the pointer on most frames, and a
/// drawing at 4K holds about a hundred thousand rectangles. Built once, on the worker that makes the
/// drawing, so a hover costs what it outlines rather than a pass over the whole picture.</para>
///
/// <para>A node with more than one shape keeps every one of them, in the order the list has them, so
/// what this answers is what a pass over the list would.</para>
/// </summary>
/// <typeparam name="TShape">A shape of the drawing: a rectangle or a sector.</typeparam>
internal sealed class NodeShapes<TShape>
{
    private const int None = -1;

    private readonly IReadOnlyList<TShape> _shapes;
    private readonly Func<TShape, int> _depthOf;

    /// <summary>The index of each node's first shape.</summary>
    private readonly Dictionary<int, int> _first;

    /// <summary>For each shape, the index of the next shape of the same node, or <see cref="None"/>.</summary>
    private readonly int[] _next;

    /// <param name="shapes">The drawing's shapes, which this reads and never copies.</param>
    /// <param name="isNode">Whether a shape is a node's own, rather than a block standing for several or for none.</param>
    /// <param name="nodeOf">The node a shape is of.</param>
    /// <param name="depthOf">How far below the drawing's root a shape sits.</param>
    public NodeShapes(IReadOnlyList<TShape> shapes, Func<TShape, bool> isNode, Func<TShape, int> nodeOf, Func<TShape, int> depthOf)
    {
        _shapes = shapes;
        _depthOf = depthOf;
        _first = new Dictionary<int, int>(shapes.Count);
        _next = new int[shapes.Count];

        // Backwards, so each node's first shape is the last one met and every chain runs in list order.
        for (var i = shapes.Count - 1; i >= 0; i--)
        {
            var shape = shapes[i];

            if (!isNode(shape))
            {
                _next[i] = None;
                continue;
            }

            var node = nodeOf(shape);

            _next[i] = _first.TryGetValue(node, out var after) ? after : None;
            _first[node] = i;
        }
    }

    /// <summary>The index of <paramref name="node"/>'s first shape, or null where it has none.</summary>
    public int? FirstOf(int node) => _first.TryGetValue(node, out var index) ? index : null;

    /// <summary>
    /// The indices of every shape of <paramref name="nodes"/>, in list order: the order the drawing
    /// paints them, a folder before what it holds.
    /// </summary>
    public List<int> Of(IReadOnlySet<int> nodes)
    {
        var found = new List<int>();

        // Asked about more nodes than there are shapes, one pass over the shapes reads less.
        if (nodes.Count >= _shapes.Count)
        {
            foreach (var (node, first) in _first)
            {
                if (nodes.Contains(node))
                {
                    AddChain(first, found);
                }
            }
        }
        else
        {
            foreach (var node in nodes)
            {
                if (_first.TryGetValue(node, out var first))
                {
                    AddChain(first, found);
                }
            }
        }

        found.Sort();

        return found;
    }

    /// <summary>
    /// The index of the deepest shape that is <paramref name="node"/> or a folder above it, up to
    /// <paramref name="root"/>, or up to the tree's own root for a node outside the drawing. The first
    /// in list order where two are as deep. Null where none of them is drawn.
    /// </summary>
    public int? Deepest(ISizedTree tree, int root, int node)
    {
        int? deepest = null;

        for (var current = node; ; current = tree.ParentOf(current))
        {
            if (_first.TryGetValue(current, out var index))
            {
                for (; index != None; index = _next[index])
                {
                    if (deepest is not { } found
                        || _depthOf(_shapes[index]) > _depthOf(_shapes[found])
                        || (_depthOf(_shapes[index]) == _depthOf(_shapes[found]) && index < found))
                    {
                        deepest = index;
                    }
                }
            }

            if (current == root || tree.ParentOf(current) == current)
            {
                return deepest;
            }
        }
    }

    private void AddChain(int first, List<int> found)
    {
        for (var index = first; index != None; index = _next[index])
        {
            found.Add(index);
        }
    }
}
