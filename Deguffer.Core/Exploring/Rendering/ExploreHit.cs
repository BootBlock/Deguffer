using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// What the pointer found: a node, the block standing in for items too small to draw, or one of the
/// blocks standing for the rest of the volume.
/// </summary>
/// <param name="Bytes">
/// What was pointed at accounts for this much. Carried rather than looked up because none of the
/// blocks has a node to look it up from.
/// </param>
public readonly record struct ExploreHit(int Node, long Bytes)
{
    public bool IsAggregate => Node == ExploreTile.Aggregated;

    public bool IsFreeSpace => Node == ExploreTile.FreeSpace;

    public bool IsUnaccounted => Node == ExploreTile.Unaccounted;

    public bool IsShadowCopies => Node == ExploreTile.ShadowCopies;

    public bool IsReservedStorage => Node == ExploreTile.ReservedStorage;

    /// <summary>
    /// Whether what was pointed at is a node of the tree, and so something a click may pick (§7.1).
    /// None of the blocks is.
    /// </summary>
    public bool IsNode => Node >= 0;
}
