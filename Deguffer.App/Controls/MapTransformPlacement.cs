using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// A <see cref="MapTransform"/> written where the compositor reads it: into a visual's <c>Scale</c>
/// and <c>Offset</c>, or into a property set's, for an expression that composes it with another.
/// </summary>
internal static class MapTransformPlacement
{
    /// <summary>Place <paramref name="visual"/> by <paramref name="transform"/>.</summary>
    public static void Place(this Visual visual, MapTransform transform)
    {
        visual.Scale = Scale(transform);
        visual.Offset = Offset(transform);
    }

    /// <summary>Write <paramref name="transform"/> into <paramref name="properties"/>' <c>Scale</c> and <c>Offset</c>.</summary>
    public static void Place(this CompositionPropertySet properties, MapTransform transform)
    {
        properties.InsertVector3(nameof(Visual.Scale), Scale(transform));
        properties.InsertVector3(nameof(Visual.Offset), Offset(transform));
    }

    /// <summary>One on the third axis, which a picture flat on the screen is never scaled along.</summary>
    private static Vector3 Scale(MapTransform transform) => new((float)transform.ScaleX, (float)transform.ScaleY, 1);

    private static Vector3 Offset(MapTransform transform) => new((float)transform.X, (float)transform.Y, 0);
}
