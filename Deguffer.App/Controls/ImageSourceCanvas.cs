using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// Names in SpaceMonger's lettering drawn into the window's image sources, which an image element
/// shows pixel for pixel.
/// </summary>
internal sealed class ImageSourceCanvas : ILabelCanvas<CanvasImageSource>
{
    public static readonly ImageSourceCanvas Instance = new();

    private ImageSourceCanvas()
    {
    }

    public CanvasImageSource Make(CanvasDevice device, float width, float height, float dpi) =>
        new(device, width, height, dpi);

    public CanvasDrawingSession Open(CanvasImageSource image) =>
        image.CreateDrawingSession(Microsoft.UI.Colors.Transparent);
}
