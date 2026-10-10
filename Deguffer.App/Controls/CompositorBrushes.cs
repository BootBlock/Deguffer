using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>How the map's visuals show a drawing's surface.</summary>
internal static class CompositorBrushes
{
    /// <summary>
    /// A brush showing <paramref name="surface"/> one pixel to one unit of the visual, from its top-left
    /// corner, so it stretches nothing itself: whatever places or scales the drawing does it through the
    /// visual, and a visual smaller than the surface shows that much of it.
    /// </summary>
    public static CompositionSurfaceBrush CreateUnstretchedBrush(this Compositor compositor, ICompositionSurface? surface = null)
    {
        var brush = compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.None;
        brush.HorizontalAlignmentRatio = 0;
        brush.VerticalAlignmentRatio = 0;

        return brush;
    }
}
