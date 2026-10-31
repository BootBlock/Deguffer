namespace Deguffer.Core.Exploring.Layout;

/// <summary>Finding one of the shapes a drawing draws in a folder (<see cref="MapShape"/>).</summary>
public static class MapShapes
{
    /// <summary>
    /// Where <paramref name="node"/>, or the block it is, stands in <paramref name="shapes"/>, or null
    /// where the drawing does not draw it there.
    /// </summary>
    public static int? IndexOf(IReadOnlyList<MapShape> shapes, int node)
    {
        ArgumentNullException.ThrowIfNull(shapes);

        for (var i = 0; i < shapes.Count; i++)
        {
            if (shapes[i].Node == node)
            {
                return i;
            }
        }

        return null;
    }
}
