using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// What the colours of one drawing say: which branch a shape belongs to, or how long ago it was last
/// written.
///
/// <para>Separate from <see cref="ExploreSurface"/> because only one of the two applies to every tree.
/// A branch is a fact about any tree's shape. An age is a fact about files, so only a scanned drive can
/// be coloured by one, and a drawing of memory has nothing to date.</para>
///
/// <para>An aggregate is coloured by neither. <see cref="ExploreSurface"/> settles that before asking,
/// because it is a run of siblings rather than a thing with a branch or a date.</para>
/// </summary>
public abstract class ShapeColours
{
    private protected ShapeColours()
    {
    }

    /// <summary>A hue per top-level branch, shaded by depth. What every tree can be coloured by.</summary>
    public static ShapeColours ByBranch { get; } = new BranchColours();

    /// <summary>A band per age, by the newest write at or below each shape, measured from <paramref name="nowUtc"/>.</summary>
    public static ShapeColours ByAge(ExploreTree tree, DateTime nowUtc) => new AgeColours(tree, nowUtc);

    /// <summary>The colours <paramref name="colouring"/> asks for, for a drawing of <paramref name="tree"/>.</summary>
    public static ShapeColours For(ExploreTree tree, ExploreColouring colouring, DateTime nowUtc) =>
        colouring == ExploreColouring.Age ? ByAge(tree, nowUtc) : ByBranch;

    internal abstract TileColour For(ExploreSurface surface, int node, int depth);

    private sealed class BranchColours : ShapeColours
    {
        internal override TileColour For(ExploreSurface surface, int node, int depth) =>
            TilePalette.For(surface.BranchOf(node), depth);
    }

    private sealed class AgeColours(ExploreTree tree, DateTime nowUtc) : ShapeColours
    {
        internal override TileColour For(ExploreSurface surface, int node, int depth) =>
            AgePalette.For(tree.ModifiedOf(node), nowUtc);
    }
}
