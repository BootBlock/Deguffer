using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The GPU canvas every drawing of a map painted on the CPU goes up through, the size of the paint
/// buffer it was painted in (see <see cref="PaintBuffers"/>). Each region goes up into its place on it
/// as the region lands, and is copied from there into the surface the drawing is shown on, so once all
/// of a drawing has landed, the whole of it is on the GPU. The overview's copy of a whole drawing (see
/// <see cref="ExploreMiniature"/>) is shrunk from it, rather than the paint buffer being sent up a
/// second time on the UI thread, 33 MB at 4K.
///
/// <para>One for each map, beside its paint buffer and let go with it, for the same reason: only one
/// drawing is ever being painted. Not one for each compositor, as <see cref="MapGraphics"/>' own
/// staging bitmap is, because two maps paint at once and each drawing has to be whole on it as it
/// arrives. Not one for each drawing either, which would keep 33 MB more for each drawing kept.</para>
/// </summary>
internal sealed class PaintUpload(MapGraphics graphics)
{
    private CanvasBitmap? _canvas;

    /// <summary>
    /// Write <paramref name="regions"/> of <paramref name="pixels"/>, a canvas <paramref name="width"/>
    /// by <paramref name="height"/>, into the same places on <paramref name="surface"/>, by way of the
    /// GPU canvas, and nothing else of it: the rest of the buffer is still being painted.
    /// </summary>
    /// <returns>
    /// The GPU canvas, which holds every region of the drawing written so far; or null where the device
    /// was lost on the way, and the drawing is made again on the next.
    /// </returns>
    public CanvasBitmap? Land(
        CompositionDrawingSurface surface,
        byte[] pixels,
        int width,
        int height,
        IReadOnlyList<CanvasRegion> regions)
    {
        // Made again only for a canvas of another size, which comes with a resize of the map, or on a
        // device that has replaced the one it was made on (G5).
        if ((_canvas = graphics.Canvas(_canvas, width, height)) is not { } canvas)
        {
            return null;
        }

        foreach (var region in regions)
        {
            graphics.Write(surface, canvas, pixels, width, region);
        }

        return canvas;
    }

    /// <summary>
    /// Let go of the canvas, for a map with nothing left to paint, or whose device was replaced: made on
    /// the old one, it would hold that device and everything on it.
    /// </summary>
    public void Release()
    {
        _canvas?.Dispose();
        _canvas = null;
    }
}
