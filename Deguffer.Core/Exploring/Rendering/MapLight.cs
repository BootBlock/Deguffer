using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// What a card beside the map lights on it while a row of the card is pointed at or has focus: one
/// folder from the list of what grew, or every shape of one kind of file from the breakdown by kind.
/// The card answers "what" and the map "where", and this is the second answer to the first.
///
/// <para>The shapes come from the drawing on screen, as an outline round a selection does, so what is
/// lit is the shape a click there resolves to (§7.1). A light names nodes of one tree and lights
/// nothing on a drawing of any other, where the same numbers are other shapes.</para>
///
/// <para>§7.1 again: a light says where something is and never that it can go. Nothing is picked by
/// it, and the selection a Delete acts on is untouched.</para>
/// </summary>
public abstract class MapLight
{
    private MapLight(ISizedTree tree) => Tree = tree;

    /// <summary>The tree the light names nodes of, and the only one it lights anything on.</summary>
    public ISizedTree Tree { get; }

    /// <summary>
    /// Light <paramref name="node"/> of <paramref name="tree"/>: its shape, or where it is not drawn
    /// one by one, the shape of the deepest folder above it that is (see
    /// <see cref="ExploreSurface.ShownAs"/>).
    /// </summary>
    public static MapLight Folder(ISizedTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return new FolderLight(tree, node);
    }

    /// <summary>
    /// Light every shape of <paramref name="tree"/> that a map coloured by type paints
    /// <paramref name="kind"/>, by the rule that paints it (<see cref="DominantTypes.KindOf"/>), so
    /// what is lit is exactly what is that colour. Only the shapes with nothing drawn inside them:
    /// see <see cref="ExploreSurface.Uncovered"/>.
    /// </summary>
    /// <param name="measured">The kind of file each node holds most of, or null where not measured yet.</param>
    public static MapLight Kind(ExploreTree tree, DominantTypes? measured, FileCategory kind)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return new KindLight(tree, measured, kind);
    }

    /// <summary>
    /// The outlines lit on <paramref name="drawing"/>, leaving out whatever <paramref name="gone"/>
    /// says was removed since the scan: the picture goes on drawing it, and it is no longer there to be
    /// found.
    ///
    /// <para>Empty where the drawing has nothing of it to light, which is an answer: what was asked
    /// for is nowhere the map draws one by one, and the map dims all of itself to say so. Null for a
    /// drawing of another tree, about which the light has nothing to say at all.</para>
    /// </summary>
    public IReadOnlyList<ExploreOutline>? On(ExploreSurface drawing, Func<int, bool> gone)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(gone);

        return ReferenceEquals(drawing.Tree, Tree) ? Lit(drawing, gone) : null;
    }

    private protected abstract IReadOnlyList<ExploreOutline> Lit(ExploreSurface drawing, Func<int, bool> gone);

    private sealed class FolderLight(ISizedTree tree, int node) : MapLight(tree)
    {
        private protected override IReadOnlyList<ExploreOutline> Lit(ExploreSurface drawing, Func<int, bool> gone) =>
            !gone(node) && drawing.ShownAs(node) is { } shown ? drawing.Outlines(new HashSet<int> { shown }) : [];
    }

    private sealed class KindLight(ExploreTree tree, DominantTypes? measured, FileCategory kind) : MapLight(tree)
    {
        private protected override IReadOnlyList<ExploreOutline> Lit(ExploreSurface drawing, Func<int, bool> gone) =>
            drawing.Uncovered(node => DominantTypes.KindOf(tree, node, measured) == kind && !gone(node));
    }
}
