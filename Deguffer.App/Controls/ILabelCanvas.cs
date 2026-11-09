using Microsoft.Graphics.Canvas;

namespace Deguffer.App.Controls;

/// <summary>
/// What a name in SpaceMonger's lettering is drawn into (see <see cref="ClassicName{TImage}"/>): the
/// window's image source (<see cref="ImageSourceCanvas"/>), or a bitmap a test reads back, because a
/// test host cannot make the window's elements.
/// </summary>
/// <typeparam name="TImage">The image the name is drawn into.</typeparam>
internal interface ILabelCanvas<TImage>
{
    /// <summary>
    /// A transparent image <paramref name="width"/> by <paramref name="height"/> device-independent
    /// pixels at <paramref name="dpi"/>, made on <paramref name="device"/>.
    /// </summary>
    TImage Make(CanvasDevice device, float width, float height, float dpi);

    /// <summary>A drawing session over the whole of <paramref name="image"/>, cleared to transparent.</summary>
    CanvasDrawingSession Open(TImage image);
}
