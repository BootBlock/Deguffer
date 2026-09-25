using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// What the colours of one drawing say: which branch a shape belongs to, or how long ago it was last
/// written.
///
/// <para>Separate from <see cref="ExploreSurface"/> because only one of the two applies to every tree.
/// A branch is a fact about any tree's shape. A last-written date is a fact about files, so only a
/// scanned drive can be coloured by one: a memory tree has no such date to band.</para>
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
    /// <param name="scheme">Which set of colours either colouring is drawn in.</param>
    /// <param name="nowUtc">What "now" is, for the age bands.</param>
    public static ShapeColours For(
        ExploreTree tree, ExploreColouring colouring, ExploreScheme scheme, DateTime nowUtc) =>
        colouring == ExploreColouring.Age ? new AgeColours(tree, scheme, nowUtc) : ByBranch(scheme);

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

    private sealed class AgeColours(ExploreTree tree, ExploreScheme scheme, DateTime nowUtc) : ShapeColours
    {
        internal override TileColour For(ExploreSurface surface, int node, int depth) =>
            AgePalette.For(tree.ModifiedOf(node), nowUtc, scheme);

        internal override void EnsureDescribes(ISizedTree drawn)
        {
            if (!ReferenceEquals(drawn, tree))
            {
                throw new ArgumentException(
                    "These age colours hold another tree's dates, so they describe nothing in this one.",
                    nameof(drawn));
            }
        }
    }
}
