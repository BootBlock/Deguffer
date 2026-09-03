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
public static class SectorRasteriser
{
    /// <summary>
    /// A sweep at or above which a sector has no angular edges to tilt away from.
    ///
    /// <para>A wedge is cushioned across its width as well as through its depth, which is what
    /// gives it an edge on each side. A sector that closes on itself has no such edge, and shading
    /// one as though it did puts a seam at twelve o'clock across an unbroken ring — most visibly on
    /// the disc in the middle, which is always a whole circle.</para>
    /// </summary>
    private const float WholeCircle = MathF.Tau - 0.0001f;

    /// <summary>
    /// Paint <paramref name="hits"/>' sunburst into <paramref name="pixels"/>, a BGRA buffer of
    /// <paramref name="width"/> × <paramref name="height"/>.
    ///
    /// <para>The buffer belongs to the caller and is overwritten in full, for the reason
    /// <see cref="TileRasteriser.Paint"/> gives (G5).</para>
    /// </summary>
    public static void Paint(
        byte[] pixels,
        SectorHitTest hits,
        int width,
        int height,
        TileColour background,
        Func<int, int> branchOf)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(branchOf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (pixels.Length < PixelBuffer.LengthFor(width, height))
        {
            throw new ArgumentException(
                $"A {width}x{height} canvas needs {PixelBuffer.LengthFor(width, height)} bytes, not {pixels.Length}.",
                nameof(pixels));
        }

        PixelBuffer.Fill(pixels, background);

        var sunburst = hits.Sunburst;
        var sectors = sunburst.Sectors;

        if (sectors.Count == 0)
        {
            return;
        }

        // One colour per sector, before a single pixel is written. Resolving it inside the loop
        // instead would walk a node's ancestors and index the palette three million times per
        // repaint on a 4K canvas, for an answer that changes only with the sector (G4). This is what
        // TileRasteriser gets for free by painting one shape at a time.
        var colours = new TileColour[sectors.Count];

        for (var i = 0; i < sectors.Count; i++)
        {
            colours[i] = sectors[i].IsAggregate
                ? TilePalette.Aggregate
                : TilePalette.For(branchOf(sectors[i].Node), sectors[i].Depth);
        }

        var left = Math.Max(0, (int)MathF.Floor(sunburst.CentreX - sunburst.Radius));
        var right = Math.Min(width, (int)MathF.Ceiling(sunburst.CentreX + sunburst.Radius));
        var top = Math.Max(0, (int)MathF.Floor(sunburst.CentreY - sunburst.Radius));
        var bottom = Math.Min(height, (int)MathF.Ceiling(sunburst.CentreY + sunburst.Radius));

        if (right <= left || bottom <= top)
        {
            return;
        }

        void Row(int y)
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
                if (hits.AtPolar(radius, angle) is not { } index)
                {
                    continue;
                }

                var (nx, ny) = Normal(sectors[index], dx, dy, radius, angle);

                CushionShading.Write(
                    pixels, ((y * width) + x) * 4, colours[index], CushionShading.LightAt(nx, ny));
            }
        }

        PixelBuffer.Rows(top, bottom, (right - left) * (bottom - top), Row);
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
        ExploreSector sector, float dx, float dy, float radius, float angle)
    {
        // An aggregate is not a thing on the disk, so it is drawn flat: a cushion would give it the
        // same physical presence as the files it stands in for.
        if (sector.IsAggregate || radius <= 0)
        {
            return (0, 0);
        }

        var ridge = CushionShading.RidgeAt(sector.Depth);

        var across = (radius - sector.InnerRadius) / (sector.OuterRadius - sector.InnerRadius);
        var radial = ridge * ((2 * across) - 1);

        var round = sector.SweepAngle >= WholeCircle
            ? 0
            : ridge * ((2 * ((angle - sector.StartAngle) / sector.SweepAngle)) - 1);

        var cosine = dx / radius;
        var sine = dy / radius;

        return ((radial * cosine) - (round * sine), (radial * sine) + (round * cosine));
    }
}
