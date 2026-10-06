using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// What the colours of one drawing say: which branch a shape belongs to, how long ago it was last
/// written, or how much it grew since an earlier scan.
///
/// <para>Separate from <see cref="ExploreSurface"/> because only one of the three applies to every
/// tree. A branch is a fact about any tree's shape. A last-written date and a growth are facts about
/// a scanned drive, so a memory tree can be coloured by neither.</para>
///
/// <para>An aggregate and a volume's free space are coloured by neither. <see cref="ExploreSurface"/>
/// settles that before asking, because neither is a thing with a branch or a date.</para>
/// </summary>
public abstract class ShapeColours
{
    private protected ShapeColours()
    {
    }

    /// <summary>One per <see cref="ExploreScheme"/>, because a branch colouring holds nothing but its scheme (G5).</summary>
    private static readonly ShapeColours[] Branches =
        [.. Enum.GetValues<ExploreScheme>().Select(scheme => new BranchColours(scheme))];

    /// <summary>
    /// A hue per folder inside its parent's, lighter by depth, in <paramref name="scheme"/>. What
    /// every tree can be coloured by.
    /// </summary>
    public static ShapeColours ByBranch(ExploreScheme scheme) => Branches[(int)scheme];

    /// <summary>
    /// The colours <paramref name="colouring"/> asks for, for a drawing of <paramref name="tree"/>.
    ///
    /// <para>Asked for again at every repaint rather than held, because the age bands are relative to
    /// the moment they are drawn: a map left open overnight would otherwise keep yesterday's answer.</para>
    ///
    /// <para>Public because the control that repaints decides when "now" is. It draws any
    /// <see cref="Layout.ISizedTree"/> and is handed a function to call for the colours, and only a
    /// caller that knows it has an <see cref="ExploreTree"/> can name these.</para>
    /// </summary>
    /// <param name="scheme">Which set of colours the branch and age colourings are drawn in.</param>
    /// <param name="nowUtc">What "now" is, for the age bands.</param>
    /// <param name="growth">
    /// What grew since the last scan of this volume, or null where there is nothing to compare with.
    /// A comparison of another tree paints nothing here as compared: its node numbers mean nothing in
    /// this one, and a snapshot of a scan still running is another tree.
    /// </param>
    public static ShapeColours For(
        ExploreTree tree, ExploreColouring colouring, ExploreScheme scheme, DateTime nowUtc, ScanGrowth? growth) =>
        colouring switch
        {
            ExploreColouring.Age => new AgeColours(tree, scheme, nowUtc),
            ExploreColouring.Growth => new GrowthColours(tree, ReferenceEquals(growth?.Tree, tree) ? growth : null),
            _ => ByBranch(scheme),
        };

    internal abstract TileColour For(ExploreSurface surface, int node, int depth);

    /// <summary>
    /// Refuse a tree this colouring cannot describe.
    ///
    /// <para>Colouring by branch describes any tree. Colouring by age describes the one tree whose
    /// dates it holds, and a node number means nothing in any other — so a drawing of memory banded by
    /// a drive's dates would read as ages of things that have none, which is the classification §7.2
    /// forbids. Before the seam that was impossible because the surface held one tree; now it is
    /// refused instead.</para>
    /// </summary>
    internal virtual void EnsureDescribes(ISizedTree tree)
    {
    }

    private sealed class BranchColours(ExploreScheme scheme) : ShapeColours
    {
        internal override TileColour For(ExploreSurface surface, int node, int depth) =>
            TilePalette.For(surface.HueOf(node), depth, scheme);
    }

    private sealed class AgeColours(ExploreTree tree, ExploreScheme scheme, DateTime nowUtc) : OneTreeColours(tree)
    {
        internal override TileColour For(ExploreSurface surface, int node, int depth) =>
            AgePalette.For(Tree.ModifiedOf(node), nowUtc, scheme);
    }

    /// <summary>
    /// A shape painted by the change that speaks for it: its own, or its nearest folder's. See
    /// <see cref="ScanGrowth.ChangeAt"/>.
    /// </summary>
    private sealed class GrowthColours(ExploreTree tree, ScanGrowth? growth) : OneTreeColours(tree)
    {
        internal override TileColour For(ExploreSurface surface, int node, int depth) =>
            GrowthPalette.For(growth?.ChangeAt(node));
    }

    /// <summary>
    /// A colouring read from facts about one scanned tree, which describes that tree and refuses any
    /// other: a node number means nothing outside the tree it came from.
    /// </summary>
    private abstract class OneTreeColours(ExploreTree tree) : ShapeColours
    {
        protected ExploreTree Tree { get; } = tree;

        internal override void EnsureDescribes(ISizedTree drawn)
        {
            if (!ReferenceEquals(drawn, Tree))
            {
                throw new ArgumentException(
                    "These colours hold another tree's facts, so they describe nothing in this one.",
                    nameof(drawn));
            }
        }
    }
}
