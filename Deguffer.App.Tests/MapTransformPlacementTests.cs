using System.Numerics;
using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.App.Tests;

/// <summary>
/// A map transform reaches the compositor as the scale and the offset it stands for, on a visual and
/// in a property set alike: the camera's, the names', and every drawing's placement go through these.
/// </summary>
public sealed class MapTransformPlacementTests
{
    private static readonly MapTransform Transform = new(ScaleX: 2.5, ScaleY: 0.75, X: -40, Y: 12.5);

    /// <summary>The axes in order, and one along the third, where a scale of nothing would hide the visual.</summary>
    [Fact]
    public void AVisualIsScaledAndMovedByTheTransform()
    {
        var (scale, offset) = CompositionThread.Run(() =>
        {
            var visual = CompositionThread.Compositor.CreateContainerVisual();

            visual.Place(Transform);

            return (visual.Scale, visual.Offset);
        });

        Assert.Equal(new Vector3(2.5f, 0.75f, 1), scale);
        Assert.Equal(new Vector3(-40, 12.5f, 0), offset);
    }

    /// <summary>Under the names an expression reads as a visual's, which is what the camera's and the names' expressions read.</summary>
    [Fact]
    public void APropertySetHoldsTheTransformAsAVisualsScaleAndOffset()
    {
        var (scale, offset) = CompositionThread.Run(() =>
        {
            var properties = CompositionThread.Compositor.CreatePropertySet();

            properties.Place(Transform);
            properties.TryGetVector3("Scale", out var placedScale);
            properties.TryGetVector3("Offset", out var placedOffset);

            return (placedScale, placedOffset);
        });

        Assert.Equal(new Vector3(2.5f, 0.75f, 1), scale);
        Assert.Equal(new Vector3(-40, 12.5f, 0), offset);
    }
}
