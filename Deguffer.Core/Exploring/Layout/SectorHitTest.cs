namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Answers "what is under the pointer" over a laid-out sunburst.
///
/// <para>No spatial grid, unlike <see cref="TileHitTest"/>, because the geometry answers directly:
/// the rings are uniform, so a radius gives the ring by one division, and the sectors within a ring
/// are sorted by angle and do not overlap, so an angle gives the sector by one binary search. That
/// is the cheapest of the three layouts' hit tests, and it is the sunburst's one clear win over
/// the other two.</para>
///
/// <para>Nesting needs no special handling here either. A child sits in the ring outside its
/// parent rather than inside its parent's area, so exactly one sector covers any point and there is
/// no deepest-wins rule to apply.</para>
/// </summary>
public sealed class SectorHitTest
{
    private readonly int[] _ringStart;

    public SectorHitTest(Sunburst sunburst)
    {
        ArgumentNullException.ThrowIfNull(sunburst);

        Sunburst = sunburst;

        // The layout emits ring by ring, so each ring is a contiguous run. Counting sort, one pass
        // to count and a prefix sum, rather than a list per ring.
        var deepest = -1;

        foreach (var sector in sunburst.Sectors)
        {
            deepest = Math.Max(deepest, sector.Depth);
        }

        _ringStart = new int[deepest + 2];

        foreach (var sector in sunburst.Sectors)
        {
            _ringStart[sector.Depth + 1]++;
        }

        for (var i = 1; i < _ringStart.Length; i++)
        {
            _ringStart[i] += _ringStart[i - 1];
        }
    }

    /// <summary>The sunburst this indexes, so a caller that has the index has the geometry too.</summary>
    public Sunburst Sunburst { get; }

    /// <summary>
    /// The index into <see cref="Layout.Sunburst.Sectors"/> of the sector covering this canvas
    /// point, or null where the point is outside the disc or in a gap within it.
    /// </summary>
    public int? At(float x, float y)
    {
        var dx = x - Sunburst.CentreX;
        var dy = y - Sunburst.CentreY;
        var radius = MathF.Sqrt((dx * dx) + (dy * dy));

        return AtPolar(radius, AngleOf(dx, dy));
    }

    /// <summary>
    /// The same answer for a point already expressed in polar coordinates about the centre.
    ///
    /// <para>Offered separately because the rasteriser resolves every pixel of the disc through
    /// this index — which is what makes what is drawn and what is reported under the pointer the
    /// same rule rather than two rules that have to be kept in step — and it needs the radius and
    /// the angle afterwards to shade with. Going back through <see cref="At"/> would compute an
    /// arctangent per pixel and then throw it away.</para>
    /// </summary>
    public int? AtPolar(float radius, float angle)
    {
        if (radius >= Sunburst.Radius)
        {
            return null;
        }

        var ring = (int)(radius / Sunburst.RingWidth);

        if (ring + 1 >= _ringStart.Length)
        {
            return null;
        }

        var sectors = Sunburst.Sectors;

        // The last sector in the ring that starts at or before this angle. No sector wraps past
        // twelve o'clock — a child is laid out inside its parent's span, and the root's span starts
        // there — so one comparison against the end settles it.
        var low = _ringStart[ring];
        var high = _ringStart[ring + 1] - 1;
        var found = -1;

        while (low <= high)
        {
            var middle = (low + high) / 2;

            if (sectors[middle].StartAngle <= angle)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0)
        {
            return null;
        }

        // A ring is not necessarily full: a directory whose children do not account for all of it
        // leaves the remainder empty, and so does a level where nothing was deep enough to draw.
        return angle < sectors[found].StartAngle + sectors[found].SweepAngle ? found : null;
    }

    /// <summary>
    /// The angle of a point relative to the centre, in radians clockwise from twelve o'clock, which
    /// is the convention <see cref="ExploreSector.StartAngle"/> is measured in.
    ///
    /// <para>The arguments are swapped and one is negated against the usual call because the canvas
    /// y axis runs downwards and the angle runs clockwise from up rather than anticlockwise from
    /// right.</para>
    /// </summary>
    public static float AngleOf(float dx, float dy)
    {
        var angle = MathF.Atan2(dx, -dy);

        return angle < 0 ? angle + MathF.Tau : angle;
    }
}
