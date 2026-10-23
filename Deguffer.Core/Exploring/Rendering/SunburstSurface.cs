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
        ISizedTree tree,
        int root,
        int width,
        int height,
        LayoutLimits limits,
        ShapeColours colours,
        MapStyle style)
        : base(tree, root, width, height, limits, colours, style, viewport: null)
    {
        _hits = new SectorHitTest(SunburstLayout.Compute(tree, root, width, height, limits));

        Labels = BuildLabels();
    }

    public override IReadOnlyList<ExploreLabel> Labels { get; }

    public override CanvasPainter Painter(TileColour background) =>
        new SectorRasteriser(_hits, Width, Height, background, (node, depth) => ColourFor(node, depth), Finish);

    public override CushionQuads? Quads() => null;

    public override ExploreHit? At(float x, float y) =>
        _hits.At(x, y) is { } index
            ? new ExploreHit(_hits.Sunburst.Sectors[index].Node, _hits.Sunburst.Sectors[index].Bytes)
            : null;

    public override bool HasVolumeBeside => false;

    /// <summary>Null: a sunburst draws the whole picture and is never zoomed.</summary>
    public override double? Ceiling => null;

    /// <summary>Null throughout: a sunburst's shapes are sectors.</summary>
    public override ExploreTile? TileAt(float x, float y) => null;

    public override IReadOnlyList<ExploreTile> Tiles => [];

    /// <summary>Null throughout, for the same reason.</summary>
    public override MapFrame? FrameOf(int node) => null;

    /// <summary>Null throughout, for the same reason.</summary>
    public override MapFrame? Locate(int node) => null;

    /// <summary>
    /// The sectors in the ring outside <paramref name="folder"/>'s that it holds, each standing at the
    /// middle of its arc. Its block of the rest is the one in that ring within the folder's own sweep.
    /// </summary>
    protected override IReadOnlyList<MapShape> FindShapes(int? folder)
    {
        var inside = folder ?? Root;
        var sunburst = _hits.Sunburst;
        var sectors = sunburst.Sectors;
        var shapes = new List<MapShape>();
        ExploreSector? holder = null;

        for (var i = 0; i < sectors.Count && holder is null; i++)
        {
            holder = !sectors[i].IsAggregate && sectors[i].Node == inside ? sectors[i] : null;
        }

        if (holder is not { } outer)
        {
            return shapes;
        }

        const float Rounding = 1e-4f;

        for (var i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];

            if (sector.Depth == outer.Depth + 1
                && (sector.IsAggregate
                    ? sector.StartAngle >= outer.StartAngle - Rounding
                        && sector.StartAngle + sector.SweepAngle <= outer.StartAngle + outer.SweepAngle + Rounding
                    : Tree.ParentOf(sector.Node) == inside))
            {
                var middle = sunburst.PointAt(sector.MidRadius, sector.MidAngle);

                shapes.Add(new MapShape(sector.Node, sector.Bytes, BoxOf(sunburst, sector), middle.X / Width, middle.Y / Height));
            }
        }

        return shapes;
    }

    /// <summary>The wedge or ring the shape stands at the middle of, traced as any outline is.</summary>
    public override IReadOnlyList<ExploreOutline> OutlineOf(MapShape shape)
    {
        var outlines = new List<ExploreOutline>();

        if (_hits.At((float)(shape.X * Width), (float)(shape.Y * Height)) is { } index)
        {
            Trace(_hits.Sunburst, _hits.Sunburst.Sectors[index], outlines);
        }

        return outlines;
    }

    /// <summary>The box round <paramref name="sector"/>, in fractions of the canvas, measured round its arcs as they are traced.</summary>
    private MapFrame BoxOf(Sunburst sunburst, ExploreSector sector)
    {
        var steps = StepsOf(sector);
        var (left, top, right, bottom) = (float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);

        for (var step = 0; step <= steps; step++)
        {
            var angle = sector.AngleAt(step, steps);

            foreach (var radius in (ReadOnlySpan<float>)[sector.InnerRadius, sector.OuterRadius])
            {
                var point = sunburst.PointAt(radius, angle);

                (left, top) = (Math.Min(left, point.X), Math.Min(top, point.Y));
                (right, bottom) = (Math.Max(right, point.X), Math.Max(bottom, point.Y));
            }
        }

        return new MapFrame(left / (double)Width, top / (double)Height, (right - left) / (double)Width, (bottom - top) / (double)Height);
    }

    /// <summary>
    /// The deepest sector that is the node or a folder above it. A folder's ring holds nothing inside
    /// it, because what it holds is drawn in the rings further out, so the deepest is the node's own
    /// sector wherever it was drawn.
    /// </summary>
    public override int? ShownAs(int node)
    {
        var path = PathOf(node);
        var sectors = _hits.Sunburst.Sectors;
        ExploreSector? deepest = null;

        for (var i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];

            if (!sector.IsAggregate && path.Contains(sector.Node) && (deepest is not { } found || sector.Depth > found.Depth))
            {
                deepest = sector;
            }
        }

        return deepest?.Node;
    }

    public override IReadOnlyList<ExploreOutline> Outlines(IReadOnlySet<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        return Traced(nodes.Contains);
    }

    /// <summary>Every sector of the nodes asked for: no sector is drawn inside another's ring.</summary>
    public override IReadOnlyList<ExploreOutline> Uncovered(Func<int, bool> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        return Traced(nodes);
    }

    /// <summary>Each node once: a sunburst draws no node twice, and every sector but a lump is one.</summary>
    public override IEnumerable<int> NodesDrawn =>
        _hits.Sunburst.Sectors.Where(sector => !sector.IsAggregate).Select(sector => sector.Node);

    public override IReadOnlyList<MorphShape> MorphShapes(IReadOnlySet<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var sunburst = _hits.Sunburst;
        var sectors = sunburst.Sectors;
        var shapes = new List<MorphShape>();

        for (var i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];

            if (!sector.IsAggregate && nodes.Contains(sector.Node))
            {
                var colour = ColourFor(sector.Node, sector.Depth);

                shapes.Add(new MorphShape(sector.Node, sector.Depth, colour, MorphOutline.Of(sunburst, sector), Finish.EdgesOf(colour, folder: false)));
            }
        }

        return shapes;
    }

    private List<ExploreOutline> Traced(Func<int, bool> nodes)
    {
        var sunburst = _hits.Sunburst;
        var sectors = sunburst.Sectors;
        var outlines = new List<ExploreOutline>();

        for (var i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];

            if (!sector.IsAggregate && nodes(sector.Node))
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
        var steps = StepsOf(sector);

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
            points.Add(sunburst.PointAt(sector.OuterRadius, sector.AngleAt(i, steps)));
        }

        if (sector.InnerRadius <= 0)
        {
            points.Add(new ExplorePoint(sunburst.CentreX, sunburst.CentreY));
        }
        else
        {
            for (var i = steps; i >= 0; i--)
            {
                points.Add(sunburst.PointAt(sector.InnerRadius, sector.AngleAt(i, steps)));
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
            points.Add(sunburst.PointAt(radius, sector.AngleAt(i, steps)));
        }

        return points;
    }

    /// <summary>
    /// How many steps an arc of <paramref name="sector"/> is traced in: one per degree of sweep. A step
    /// cuts inside the true arc by under four hundred-thousandths of its radius, which at the radii a
    /// sunburst is drawn at is a few hundredths of a pixel: far below what a stroke a pixel or two wide
    /// can show.
    /// </summary>
    private static int StepsOf(ExploreSector sector) =>
        Math.Clamp((int)MathF.Ceiling(sector.SweepAngle / (MathF.PI / 180)), 2, 360);

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

        // Every name on a sunburst is inside its own sector, so a style that writes none writes nothing.
        if (!Style.ShapeNames)
        {
            return labels;
        }

        var sectors = sunburst.Sectors;

        for (var i = 0; i < sectors.Count && labels.Count < MaximumLabels; i++)
        {
            var sector = sectors[i];

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
            var middle = sunburst.PointAt(sector.MidRadius, angle);
            var width = chord - (Limits.LabelPadding * 2);
            var left = middle.X - (width / 2);
            var top = middle.Y - (Limits.MinimumLabelHeight / 2);

            // The room is the label's own box, unturned. A sunburst is never magnified, so the room
            // is only ever asked whether the text fits where it was drawn, which the chord decided.
            labels.Add(new ExploreLabel(
                sector.Node,
                left,
                top,
                width,
                RotationAt(angle),
                Centred: true,
                TextColourFor(sector.Node, sector.Depth),
                sector.Bytes,
                new LabelRoom(left, top, left + width, top + Limits.MinimumLabelHeight)));
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
