using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>Paints a whole canvas the way a redraw does, so a test of a picture tests the shipped path.</summary>
internal static class Painting
{
    /// <summary>Paint every region of the canvas into <paramref name="pixels"/>, in the order a redraw puts them up.</summary>
    public static void PaintRegions(this CanvasPainter painter, byte[] pixels)
    {
        foreach (var region in PaintOrder.Regions(painter.Width, painter.Height, focus: null))
        {
            painter.Paint(pixels, region);
        }
    }
}
