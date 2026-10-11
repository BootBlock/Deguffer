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
