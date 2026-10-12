namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Which folder's shapes hold a shape on the map, what a shape is a share of, and which shape the
/// keyboard is on after going up: the tree's side of the shapes the keyboard and a screen reader
/// reach (<see cref="Rendering.ExploreSurface.Shapes"/>).
///
/// <para>Each answer is about a picture of <c>drawn</c>, a node of the tree, drawn alone or, where
/// <c>beside</c>, as the root of a whole volume with the space that is not in it beside it. Then the
/// root is one of the shapes at the top of the picture, and what it holds is its own, as any folder's
/// is.</para>
/// </summary>
public static class MapFolders
{
    /// <summary>
    /// The folder whose shapes hold <paramref name="node"/>'s, or null where it is at the top of the
    /// picture: what the drawn node holds, or beside a volume the drawn node itself.
    /// </summary>
    public static int? Of(ISizedTree tree, int drawn, bool beside, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (beside && node == drawn)
        {
            return null;
        }

        var parent = tree.ParentOf(node);

        return !beside && parent == drawn ? null : parent;
    }

    /// <summary>
    /// Whether a screen reader is given <paramref name="folder"/>'s shapes under the folder's own shape:
    /// where the folder is the one whose shapes the keyboard moves between, or holds it at any depth, so
    /// the shape the keyboard is on is in the tree with everything above it; and where the reader
    /// expanded it. Any other folder's shape is a collapsed item, as a tree view's is.
    ///
    /// <para>So a search of the window meets the shapes along one path and those the reader opened,
    /// rather than every shape of the picture. A picture of a large tree draws tens of thousands, and a
    /// search asks the window across processes for each element it meets.</para>
    /// </summary>
    /// <param name="keyboard">The folder whose shapes the keyboard moves between, or null for the top of the picture.</param>
    /// <param name="expanded">The folders the reader expanded.</param>
    public static bool Lists(ISizedTree tree, int drawn, bool beside, int folder, int? keyboard, IReadOnlySet<int> expanded)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(expanded);

        if (expanded.Contains(folder))
        {
            return true;
        }

        // Up through the folders holding the keyboard's, which ends at the top of the picture, or at
        // the tree's root where the keyboard's folder is not in this picture at all.
        for (var at = keyboard; at is { } inside; at = Of(tree, drawn, beside, inside) is var above && above != inside ? above : null)
        {
            if (inside == folder)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The shapes a screen reader is given under <paramref name="folder"/>'s own shape in
    /// <paramref name="drawing"/>: the folder's shapes where <see cref="Lists"/> says so, and none
    /// otherwise. What the map's tree lists under a shape, so the rule bounds the tree itself rather than
    /// a copy of it.
    /// </summary>
    public static IReadOnlyList<MapShape> Listed(Rendering.ExploreSurface drawing, int drawn, int folder, int? keyboard, IReadOnlySet<int> expanded)
    {
        ArgumentNullException.ThrowIfNull(drawing);

        return Lists(drawing.Tree, drawn, drawing.HasVolumeBeside, folder, keyboard, expanded) ? drawing.Shapes(folder) : [];
    }

    /// <summary>
    /// What a shape among <paramref name="folder"/>'s shapes is a share of: the folder, how much it
    /// holds and which node names it; at the top of the picture, the drawn node; and beside a volume,
    /// everything the top of the picture draws, which is the drive, named by no node.
    /// </summary>
    /// <param name="top">The shapes at the top of the picture, which beside a volume are the whole of the drive.</param>
    public static (long Bytes, int? Named) ShareBasis(ISizedTree tree, int drawn, bool beside, int? folder, IReadOnlyList<MapShape> top)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(top);

        return folder switch
        {
            { } inside => (tree.SizeOf(inside), inside),
            null when beside => (top.Sum(shape => shape.Bytes), null),
            null => (tree.SizeOf(drawn), drawn),
        };
    }

    /// <summary>
    /// The shape at the top of <paramref name="node"/>'s picture that the keyboard is on after coming up
    /// to it from <paramref name="from"/>: the shape of what holds <paramref name="from"/>, so the reader
    /// is where they came out; or, coming from the root to the root drawn beside its volume, the root.
    /// Null where <paramref name="from"/> is not below it, as for a step down, which starts at the first.
    /// </summary>
    public static int? CameUpFrom(ISizedTree tree, int from, int node, bool beside)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (from == node)
        {
            return beside ? node : null;
        }

        for (var at = from; tree.ParentOf(at) != at; at = tree.ParentOf(at))
        {
            if (tree.ParentOf(at) == node)
            {
                return at;
            }
        }

        return null;
    }
}
