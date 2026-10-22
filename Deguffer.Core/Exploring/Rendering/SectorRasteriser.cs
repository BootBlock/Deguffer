using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Draws a laid-out sunburst into a pixel buffer.
///
/// <para>Pixel by pixel rather than sector by sector, which is the opposite of
/// <see cref="TileRasteriser"/> and is what the geometry wants. An annular sector has no pixel
/// bounds to walk — its bounding box is mostly outside it — so filling one means asking, for each
/// pixel in the box, whether it is inside. Asking that question once per pixel of the disc instead
/// answers it for every sector at the same time, and each pixel is written exactly once.</para>
///
/// <para>The question is asked of <see cref="SectorHitTest"/>, the same index the pointer goes
/// through. What is drawn and what is reported under the pointer are therefore one rule rather
/// than two that have to be kept in step.</para>
/// </summary>
public sealed class SectorRasteriser : CanvasPainter
{
    private readonly SectorHitTest _hits;

    private readonly TileColour[] _colours;

    /// <summary>How every sector is finished. See <see cref="ShapeFinish"/>.</summary>
    private readonly ShapeFinish _finish;

    /// <summary>
    /// Get ready to paint <paramref name="hits"/>' sunburst on a canvas of <paramref name="width"/>
    /// by <paramref name="height"/>, each sector in the colour <paramref name="colourOf"/> gives its
    /// node at its depth.
    /// </summary>
    /// <param name="finish">
    /// How every sector is finished: its line and its bevel inside its rings and its sides, as a quad's
    /// are inside its sides. Every sector is a block, because a folder's sector sits beside what it
    /// holds rather than round it.
    /// </param>
    public SectorRasteriser(
        SectorHitTest hits,
        int width,
        int height,
        TileColour background,
        Func<int, int, TileColour> colourOf,
        ShapeFinish finish)
        : base(width, height, background)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(colourOf);

        _hits = hits;
        _finish = finish;

        // One colour per sector, before a single pixel is written. Resolving it per pixel instead
        // would walk a node's ancestors and index the palette three million times per repaint on a
        // 4K canvas, for an answer that changes only with the sector (G4). TileRasteriser hoists
        // its colours the same way, for the same reason.
        var sectors = hits.Sunburst.Sectors;

        _colours = new TileColour[sectors.Count];

        for (var i = 0; i < sectors.Count; i++)
        {
            _colours[i] = colourOf(sectors[i].Node, sectors[i].Depth);
        }
    }

    /// <summary>Paint the part of the disc inside <paramref name="region"/>.</summary>
    protected override void Draw(byte[] pixels, CanvasRegion region)
    {
        var sunburst = _hits.Sunburst;
        var sectors = sunburst.Sectors;

        if (sectors.Count == 0)
        {
            return;
        }

        var left = Math.Max(region.X, (int)MathF.Floor(sunburst.CentreX - sunburst.Radius));
        var right = Math.Min(region.Right, (int)MathF.Ceiling(sunburst.CentreX + sunburst.Radius));
        var top = Math.Max(region.Y, (int)MathF.Floor(sunburst.CentreY - sunburst.Radius));
        var bottom = Math.Min(region.Bottom, (int)MathF.Ceiling(sunburst.CentreY + sunburst.Radius));

        for (var y = top; y < bottom; y++)
        {
            // Sampled at the middle of the pixel rather than at its corner. On a corner every
            // sector is measured half a pixel too far towards the top left of the canvas, and the
            // disc comes out lopsided against the ring boundaries by that much.
            var dy = y + 0.5f - sunburst.CentreY;

            for (var x = left; x < right; x++)
            {
                var dx = x + 0.5f - sunburst.CentreX;
                var radius = MathF.Sqrt((dx * dx) + (dy * dy));

                if (radius >= sunburst.Radius)
                {
                    continue;
                }

                var angle = SectorHitTest.AngleOf(dx, dy);

                // The ground is already in the buffer, so a gap in a ring needs nothing drawn.
                if (_hits.AtPolar(radius, angle) is not { } index)
                {
                    continue;
                }

                Paint(pixels, ((y * Width) + x) * 4, sectors[index], _colours[index], dx, dy, radius, angle);
            }
        }
    }

    /// <summary>
    /// Paint one pixel of <paramref name="sector"/>, filled <paramref name="colour"/>, finished as the
    /// drawing says: its line inside the sector's edges, then its bevel, then its cushion or its own
    /// colour.
    /// </summary>
    private void Paint(byte[] pixels, int offset, ExploreSector sector, TileColour colour, float dx, float dy, float radius, float angle)
    {
        var line = _finish.Borders.WidthFor(folder: false);

        // An aggregate is not a thing on the disk, so it has no bevel, as it has no cushion.
        var bevelled = _finish.Shading == MapShading.Bevel && !sector.IsAggregate;
        var band = line + (bevelled ? _finish.Bevel.Width : 0);

        if (band > 0 && Nearest(sector, radius, angle) is var (distance, edge) && distance < band)
        {
            PixelBuffer.Write(
                pixels,
                offset,
                distance < line
                    ? ShapeFinish.Mix(colour, _finish.Borders.Tone, _finish.Borders.AmountFor(folder: false))
                    : Faces(edge, dx, dy, radius) ? _finish.Lit(colour) : _finish.Shaded(colour));
            return;
        }

        if (_finish.Shading != MapShading.Cushion)
        {
            PixelBuffer.Write(pixels, offset, colour);
            return;
        }

        var (nx, ny) = Normal(sector, dx, dy, radius, angle, _finish.Relief);

        CushionShading.Write(pixels, offset, colour, CushionShading.LightAt(nx, ny));
    }

    /// <summary>
    /// How far a point at <paramref name="radius"/> and <paramref name="angle"/> is from the nearest of
    /// <paramref name="sector"/>'s edges, and which edge that is: its outer ring, its inner ring where
    /// it has one, and its two sides where it is not a whole ring. A side is measured along the arc, so
    /// a line is as wide near the middle as at the rim.
    /// </summary>
    private static (float Distance, SectorEdge Edge) Nearest(ExploreSector sector, float radius, float angle)
    {
        var nearest = (Distance: sector.OuterRadius - radius, Edge: SectorEdge.Outer);

        if (sector.InnerRadius > 0 && radius - sector.InnerRadius < nearest.Distance)
        {
            nearest = (radius - sector.InnerRadius, SectorEdge.Inner);
        }

        if (!sector.IsWholeCircle)
        {
            var start = (angle - sector.StartAngle) * radius;
            var end = (sector.StartAngle + sector.SweepAngle - angle) * radius;

            if (start < nearest.Distance)
            {
                nearest = (start, SectorEdge.Start);
            }

            if (end < nearest.Distance)
            {
                nearest = (end, SectorEdge.End);
            }
        }

        return nearest;
    }

    /// <summary>
    /// Whether <paramref name="edge"/> faces the light, above and to the left, at a point
    /// <paramref name="dx"/> and <paramref name="dy"/> from the middle of the disc: whether the way out
    /// of the sector across that edge leads up and to the left. A rectangle's top and left sides face
    /// it and its bottom and right do not, and this is the same rule round a curve.
    /// </summary>
    private static bool Faces(SectorEdge edge, float dx, float dy, float radius)
    {
        if (radius <= 0)
        {
            return false;
        }

        var cosine = dx / radius;
        var sine = dy / radius;

        // Out across each edge, in canvas directions. An angle grows towards (-sine, cosine), so the
        // start side is crossed going the other way and the end side going that way.
        var (outX, outY) = edge switch
        {
            SectorEdge.Inner => (-cosine, -sine),
            SectorEdge.Start => (sine, -cosine),
            SectorEdge.End => (-sine, cosine),
            _ => (cosine, sine),
        };

        return outX + outY < 0;
    }

    /// <summary>
    /// How far the cushion's surface tilts at this point, in canvas directions.
    ///
    /// <para>The same parabolic cushion the rectangles get, in the sector's own two directions:
    /// across the ring, and round it. Those two are perpendicular to each other at every point, so
    /// the tilt is turned into canvas directions by rotating it by the point's own angle — and the
    /// sine and cosine of that angle are the point's own offsets divided by its radius, so the
    /// rotation costs no trigonometry.</para>
    /// </summary>
    private static (double X, double Y) Normal(
        ExploreSector sector, float dx, float dy, float radius, float angle, float relief)
    {
        // An aggregate is not a thing on the disk, so it is drawn flat: a cushion would give it the
        // same physical presence as the files it stands in for.
        if (sector.IsAggregate || radius <= 0)
        {
            return (0, 0);
        }

        var ridge = CushionShading.RidgeAt(sector.Depth) * relief;

        var across = (radius - sector.InnerRadius) / (sector.OuterRadius - sector.InnerRadius);
        var radial = ridge * ((2 * across) - 1);

        // A wedge is cushioned across its width as well as through its depth, which is what gives
        // it an edge on each side. A ring has no such edge, and shading one as though it did puts a
        // seam at twelve o'clock across unbroken colour.
        var round = sector.IsWholeCircle
            ? 0
            : ridge * ((2 * ((angle - sector.StartAngle) / sector.SweepAngle)) - 1);

        var cosine = dx / radius;
        var sine = dy / radius;

        return ((radial * cosine) - (round * sine), (radial * sine) + (round * cosine));
    }

    /// <summary>Which of a sector's edges a point is nearest.</summary>
    private enum SectorEdge
    {
        Outer,
        Inner,
        Start,
        End,
    }
}
