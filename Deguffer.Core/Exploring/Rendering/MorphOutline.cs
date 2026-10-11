using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// A shape's outline as a change of view carries it (<see cref="ViewMorph"/>): the same number of
/// points in every view, traced in the same order, so the outline of a shape in one view can be
/// blended into its outline in another point by point.
///
/// <para>Every shape is traced as a patch with an inner edge and an outer edge: along the inner edge
/// from its start to its end, then back along the outer edge. A sector's inner edge is its inner arc
/// and its start is where its sweep starts. A rectangle's inner edge is its top and its start is its
/// left, which is how an icicle's bar becomes the sunburst's wedge of the same node: the top of a bar,
/// the side nearest the root, is the inner arc of the wedge, and a bar's left is where the wedge
/// starts, as both go round the root in the same order. A treemap's rectangle has no such sense, and
/// is traced the same way so that its corners go to the corners of the wedge or the bar.</para>
/// </summary>
public static class MorphOutline
{
    /// <summary>
    /// How many segments each edge is traced in. Enough for the widest arc a sunburst draws, a whole
    /// turn, to stay within half a pixel of the true circle at a radius of 800 pixels. A straight edge
    /// is traced in as many, because the edge it becomes in another view may be an arc.
    /// </summary>
    public const int Steps = 96;

    /// <summary>How many points every outline has: each edge's ends and the points between them.</summary>
    public const int Points = 2 * (Steps + 1);

    /// <summary>The outline of <paramref name="tile"/>, in the canvas pixels it was laid out in.</summary>
    public static IReadOnlyList<ExplorePoint> Of(ExploreTile tile)
    {
        var points = new ExplorePoint[Points];
        var bottom = tile.Y + tile.Height;

        for (var i = 0; i <= Steps; i++)
        {
            var x = tile.X + (tile.Width * i / Steps);

            points[i] = new ExplorePoint(x, tile.Y);
            points[Points - 1 - i] = new ExplorePoint(x, bottom);
        }

        return points;
    }

    /// <summary>The outline of <paramref name="sector"/>, measured about the centre of <paramref name="sunburst"/>.</summary>
    public static IReadOnlyList<ExplorePoint> Of(Sunburst sunburst, ExploreSector sector)
    {
        ArgumentNullException.ThrowIfNull(sunburst);

        var points = new ExplorePoint[Points];

        for (var i = 0; i <= Steps; i++)
        {
            var angle = sector.StartAngle + (sector.SweepAngle * i / Steps);

            points[i] = sunburst.PointAt(sector.InnerRadius, angle);
            points[Points - 1 - i] = sunburst.PointAt(sector.OuterRadius, angle);
        }

        return points;
    }
}
