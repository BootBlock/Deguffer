namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One annular sector a sunburst layout produced, in the canvas the layout was asked for.
///
/// <para>The polar counterpart of <see cref="ExploreTile"/>, and separate from it for the same
/// reason the two are separate everywhere else: a rectangle and a sector are not the same value,
/// and a record whose four numbers mean corners in one view and radii in another is a record that
/// has to be read alongside the caller to be understood.</para>
/// </summary>
/// <param name="Node">
/// The node in the tree, or <see cref="Aggregated"/> where this sector stands for several siblings
/// too narrow to draw individually.
/// </param>
/// <param name="Depth">
/// How far below the layout's root this sits, which is also which ring it occupies. The root itself
/// is zero, and it is the disc in the middle rather than a ring.
/// </param>
/// <param name="Bytes">What this sector represents. Carried for the reason <see cref="ExploreTile.Bytes"/> is.</param>
/// <param name="StartAngle">
/// Where the sector begins, in radians clockwise from twelve o'clock. That origin is what every
/// tool in this category uses, and it is the one a reader assumes when asked to compare two wedges.
/// </param>
/// <param name="SweepAngle">How far round it goes, in radians. A whole circle is 2π.</param>
public readonly record struct ExploreSector(
    int Node,
    int Depth,
    long Bytes,
    float InnerRadius,
    float OuterRadius,
    float StartAngle,
    float SweepAngle)
{
    /// <summary>
    /// The node number of a sector standing in for omitted siblings. The same value
    /// <see cref="ExploreTile.Aggregated"/> uses, and for the reasoning behind aggregating at all,
    /// see that constant.
    /// </summary>
    public const int Aggregated = ExploreTile.Aggregated;

    public bool IsAggregate => Node == Aggregated;

    /// <summary>Half way across the ring, which is where a label sits.</summary>
    public float MidRadius => (InnerRadius + OuterRadius) / 2;

    /// <summary>Half way round the sector, which is the direction a label sits in.</summary>
    public float MidAngle => StartAngle + (SweepAngle / 2);
}

/// <summary>
/// A laid-out sunburst: the sectors, and the geometry every one of them is measured against.
///
/// <para>Carried together because a sector on its own cannot be drawn or pointed at — its radii and
/// angles are relative to a centre it does not know. Handing the two around separately is how a
/// picture comes to be drawn about one centre and hit-tested about another.</para>
/// </summary>
/// <param name="RingWidth">
/// How wide every ring is. Uniform, which is what lets a radius be turned into a ring number by one
/// division rather than by a search.
/// </param>
/// <param name="Radius">The outer edge of the outermost ring that was drawn.</param>
public sealed record Sunburst(
    IReadOnlyList<ExploreSector> Sectors,
    float CentreX,
    float CentreY,
    float RingWidth,
    float Radius);
