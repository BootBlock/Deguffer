namespace Deguffer.Core.Exploring.Layout;

/// <summary>Which way an arrow key moves the keyboard's focus across the map.</summary>
public enum MapDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// Which shape is next to another in a direction, for the keyboard moving between the shapes of one
/// folder on the map (<see cref="MapShape"/>).
///
/// <para>A shape is a candidate where the point it stands at is past the point the shape left stands
/// at, in that direction, and it reaches further that way than the shape left does: up from a shape as
/// tall as its folder is nowhere, not to the shape beside it whose middle is higher. Of those, the
/// nearest wins: the gap between the two along the way, with the
/// distance between them across the way counted twice, so a shape in line beats a nearer one off to
/// the side; and between equals, the one whose point is nearer. Measured in pixels, because a canvas
/// is rarely square and a fraction across it is not a fraction down it.</para>
///
/// <para>The same rule for every view. A treemap's rectangles share their edges, so its gaps are
/// mostly nothing and the distance across decides; a sunburst's wedges have boxes that overlap, so the
/// points the arcs stand at decide.</para>
/// </summary>
public static class MapNeighbours
{
    /// <summary>
    /// How much more the distance across the way counts than the gap along it. Two: a shape straight
    /// ahead and a little further beats one beside the way and a little nearer, which is what an arrow
    /// key is taken to mean.
    /// </summary>
    private const double Across = 2;

    /// <summary>
    /// The index of the shape next to <paramref name="shapes"/>[<paramref name="from"/>] in
    /// <paramref name="direction"/>, on a canvas <paramref name="width"/> by <paramref name="height"/>
    /// pixels; or null where there is none that way, which is where the keyboard stays put.
    /// </summary>
    public static int? Toward(IReadOnlyList<MapShape> shapes, int from, MapDirection direction, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(from, shapes.Count);

        var origin = shapes[from];
        int? nearest = null;
        var best = (Score: double.MaxValue, Distance: double.MaxValue);

        for (var i = 0; i < shapes.Count; i++)
        {
            var shape = shapes[i];
            var (along, aside) = Apart(direction, (shape.X - origin.X) * width, (shape.Y - origin.Y) * height);

            if (i == from || along <= 0 || !Further(direction, origin.Frame, shape.Frame, width, height))
            {
                continue;
            }

            var score = Gap(direction, origin.Frame, shape.Frame, width, height) + (Across * Aside(direction, origin.Frame, shape.Frame, width, height));
            var distance = (along * along) + (aside * aside);

            if (score < best.Score || (score == best.Score && distance < best.Distance))
            {
                nearest = i;
                best = (score, distance);
            }
        }

        return nearest;
    }

    /// <summary>How far a point <paramref name="x"/>, <paramref name="y"/> pixels away is along the way and across it.</summary>
    private static (double Along, double Aside) Apart(MapDirection direction, double x, double y) => direction switch
    {
        MapDirection.Left => (-x, y),
        MapDirection.Right => (x, y),
        MapDirection.Up => (-y, x),
        _ => (y, x),
    };

    /// <summary>
    /// Whether <paramref name="to"/>'s far side is past <paramref name="from"/>'s that way by more than
    /// a hair of a pixel, which the rounding of shapes that share an edge stays within.
    /// </summary>
    private static bool Further(MapDirection direction, MapFrame from, MapFrame to, double width, double height) => direction switch
    {
        MapDirection.Left => (from.X - to.X) * width,
        MapDirection.Right => (to.X + to.Width - (from.X + from.Width)) * width,
        MapDirection.Up => (from.Y - to.Y) * height,
        _ => (to.Y + to.Height - (from.Y + from.Height)) * height,
    } > Hair;

    /// <summary>A hundredth of a pixel.</summary>
    private const double Hair = 0.01;

    /// <summary>The gap between the near side of <paramref name="to"/> and the far side of <paramref name="from"/> along the way, or none where they overlap.</summary>
    private static double Gap(MapDirection direction, MapFrame from, MapFrame to, double width, double height) => Math.Max(0, direction switch
    {
        MapDirection.Left => (from.X - (to.X + to.Width)) * width,
        MapDirection.Right => (to.X - (from.X + from.Width)) * width,
        MapDirection.Up => (from.Y - (to.Y + to.Height)) * height,
        _ => (to.Y - (from.Y + from.Height)) * height,
    });

    /// <summary>The gap between the two across the way, or none where they are side by side for any of it.</summary>
    private static double Aside(MapDirection direction, MapFrame from, MapFrame to, double width, double height) =>
        direction is MapDirection.Left or MapDirection.Right
            ? Math.Max(0, Math.Max(from.Y, to.Y) - Math.Min(from.Y + from.Height, to.Y + to.Height)) * height
            : Math.Max(0, Math.Max(from.X, to.X) - Math.Min(from.X + from.Width, to.X + to.Width)) * width;
}
