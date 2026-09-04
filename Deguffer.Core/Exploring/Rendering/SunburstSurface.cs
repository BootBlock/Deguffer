using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The drawing behind the sunburst view: <see cref="SunburstLayout"/>'s rings, painted and pointed
/// at through the one index, and labelled along the ring rather than across it.
/// </summary>
public sealed class SunburstSurface : ExploreSurface
{
    private readonly SectorHitTest _hits;

    public SunburstSurface(
        ExploreTree tree,
        int root,
        int width,
        int height,
        LayoutLimits limits,
        ExploreColouring colouring,
        DateTime nowUtc)
        : base(tree, root, width, height, limits, colouring, nowUtc)
    {
        _hits = new SectorHitTest(SunburstLayout.Compute(tree, root, width, height, limits));

        Labels = BuildLabels();
    }

    public override IReadOnlyList<ExploreLabel> Labels { get; }

    public override void Paint(byte[] pixels, TileColour background) =>
        SectorRasteriser.Paint(pixels, _hits, Width, Height, background, ColourFor);

    public override ExploreHit? At(float x, float y) =>
        _hits.At(x, y) is { } index
            ? new ExploreHit(_hits.Sunburst.Sectors[index].Node, _hits.Sunburst.Sectors[index].Bytes)
            : null;

    public override IReadOnlyList<ExploreOutline> Outlines(IReadOnlySet<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var sunburst = _hits.Sunburst;
        var sectors = sunburst.Sectors;
        var outlines = new List<ExploreOutline>();

        for (var i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];

            if (!sector.IsAggregate && nodes.Contains(sector.Node))
            {
                Trace(sunburst, sector, outlines);
            }
        }

        return outlines;
    }

    /// <summary>
    /// The boundary of one sector, added to <paramref name="outlines"/>.
    ///
    /// <para>Three shapes, not one. A <b>wedge</b> is a single closed shape: out along the far edge
    /// and back along the near one. The <b>disc</b> in the middle has no near edge, so its rim is
    /// the whole of it. A <b>ring</b> — a sector that closes on itself with something inside it,
    /// which is what a directory holding one child produces — has a boundary in two separate
    /// pieces, and that is why this adds rather than returns.</para>
    ///
    /// <para>The ring is the case worth stating. Its two circles cannot be joined into one shape
    /// without a radial line across the ring at twelve o'clock, which reads as a division that is
    /// not there; and outlining only its outer circle draws a line round the hole as well as round
    /// the ring, marking out a shape the user did not pick (§7.1).</para>
    /// </summary>
    private static void Trace(Sunburst sunburst, ExploreSector sector, List<ExploreOutline> outlines)
    {
        // One point per degree of sweep. At the radii a sunburst is drawn at that is a hundredth of
        // a pixel from the true arc, which is far below what a stroke a pixel or two wide can show.
        var steps = Math.Clamp((int)MathF.Ceiling(sector.SweepAngle / (MathF.PI / 180)), 2, 360);

        if (sector.IsWholeCircle)
        {
            outlines.Add(new ExploreOutline(sector.Node, Circle(sunburst, sector, sector.OuterRadius, steps)));

            if (sector.InnerRadius > 0)
            {
                outlines.Add(new ExploreOutline(sector.Node, Circle(sunburst, sector, sector.InnerRadius, steps)));
            }

            return;
        }

        var points = new List<ExplorePoint>((steps + 1) * 2);

        for (var i = 0; i <= steps; i++)
        {
            points.Add(At(sunburst, sector.OuterRadius, Angle(sector, i, steps)));
        }

        if (sector.InnerRadius <= 0)
        {
            points.Add(new ExplorePoint(sunburst.CentreX, sunburst.CentreY));
        }
        else
        {
            for (var i = steps; i >= 0; i--)
            {
                points.Add(At(sunburst, sector.InnerRadius, Angle(sector, i, steps)));
            }
        }

        outlines.Add(new ExploreOutline(sector.Node, points));
    }

    /// <summary>
    /// A closed circle at this radius. One point short of a full turn, because the shape closes on
    /// itself and repeating the start as the end would leave a segment of no length in it.
    /// </summary>
    private static IReadOnlyList<ExplorePoint> Circle(
        Sunburst sunburst, ExploreSector sector, float radius, int steps)
    {
        var points = new List<ExplorePoint>(steps);

        for (var i = 0; i < steps; i++)
        {
            points.Add(At(sunburst, radius, Angle(sector, i, steps)));
        }

        return points;
    }

    /// <summary>Where step <paramref name="step"/> of <paramref name="steps"/> falls across the sweep.</summary>
    private static float Angle(ExploreSector sector, int step, int steps) =>
        sector.StartAngle + (sector.SweepAngle * step / steps);

    /// <summary>
    /// A point on the canvas at this radius and angle. Angles run clockwise from twelve o'clock, and
    /// the canvas's y axis runs downwards, which is where the subtraction comes from.
    /// </summary>
    private static ExplorePoint At(Sunburst sunburst, float radius, float angle) => new(
        sunburst.CentreX + (radius * MathF.Sin(angle)),
        sunburst.CentreY - (radius * MathF.Cos(angle)));

    /// <summary>
    /// Put each label along its own ring, turned so it lies with the sector rather than across it.
    ///
    /// <para>Turned, and straight. Text that follows the curve is what the reference tools draw and
    /// it is the hardest of the three things to do well — every glyph needs its own transform, and
    /// the result is least readable exactly where a ring is tightest. A straight line along the
    /// tangent reads as well as horizontal text through most of the circle, and it is one transform
    /// on one text control, which keeps the label a real control a screen reader can reach.</para>
    ///
    /// <para>Nothing is ever drawn upside down: past a quarter turn the label is turned back the
    /// other way, which is why the left half of the picture reads bottom-to-top.</para>
    ///
    /// <para>Unlike the rectangular views there is no covering to allow for. A child sits in the
    /// ring outside its parent rather than inside it, so a parent's label is never overprinted and
    /// the only question is whether the sector is big enough to hold one.</para>
    /// </summary>
    private IReadOnlyList<ExploreLabel> BuildLabels()
    {
        // Only the width has to be checked. A ring is never narrower than
        // LayoutLimits.RowHeight, which is the smallest band a level may be drawn in at all, so
        // there is always room across the ring for one line of text.
        var sunburst = _hits.Sunburst;
        var labels = new List<ExploreLabel>();

        foreach (var sector in sunburst.Sectors)
        {
            if (labels.Count >= MaximumLabels)
            {
                break;
            }

            // The disc in the middle is the node the page is already showing, and the breadcrumb
            // above the picture names it.
            if (sector.IsAggregate || sector.Depth == 0)
            {
                continue;
            }

            // The straight line the text sits on, which is the chord rather than the arc. Past half
            // a turn a wider sweep does not give a longer chord, so the sweep is capped there — a
            // sector that closes on itself is as wide across as a semicircle is.
            var chord = 2 * sector.MidRadius * MathF.Sin(MathF.Min(sector.SweepAngle, MathF.PI) / 2);

            if (chord < Limits.MinimumLabelWidth)
            {
                continue;
            }

            var angle = sector.MidAngle;
            var middle = At(sunburst, sector.MidRadius, angle);
            var width = chord - (Limits.LabelPadding * 2);

            labels.Add(new ExploreLabel(
                sector.Node,
                middle.X - (width / 2),
                middle.Y - (Limits.MinimumLabelHeight / 2),
                width,
                RotationAt(angle),
                Centred: true,
                TextColourFor(sector.Node, sector.Depth)));
        }

        return labels;
    }

    /// <summary>
    /// How far to turn a label lying along the ring at this angle, in degrees clockwise.
    ///
    /// <para>The tangent at an angle measured clockwise from twelve o'clock is that same angle away
    /// from horizontal, so the turn is the angle itself, brought back into a quarter turn each way.
    /// That is what keeps the text upright: the right half of the picture reads top-to-bottom and
    /// the left half bottom-to-top, and nothing is ever inverted.</para>
    /// </summary>
    private static float RotationAt(float angle)
    {
        var degrees = angle * 180 / MathF.PI;

        return degrees switch
        {
            > 90 and < 270 => degrees - 180,
            >= 270 => degrees - 360,
            _ => degrees,
        };
    }
}
