using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Microsoft.Graphics.Canvas;

namespace Deguffer.App.Controls;

/// <summary>
/// One redraw landing in one of a map's layers (<see cref="ExploreLayers"/>): the layer it took, and
/// what it does once it has arrived. See <see cref="ICanvasRedrawTarget"/> for the order of the calls,
/// and <see cref="Replacement"/> and <see cref="Underlay"/> for the two places a redraw lands.
/// </summary>
internal sealed class ExploreLanding : ICanvasRedrawTarget
{
    private readonly ExploreLayers _layers;

    /// <summary>
    /// Whether it replaces the drawing the map works from, or lands under it in its place by how far it
    /// is zoomed.
    /// </summary>
    private readonly bool _onTop;

    /// <summary>Told once all of the drawing has landed.</summary>
    private readonly Action<ExploreSurface> _arrived;

    /// <summary>How a drawing on top comes in over the one it replaces.</summary>
    private readonly Motion _fade;

    /// <summary>What the drawing goes up to the GPU through, where it is painted on the CPU: the map's own.</summary>
    private readonly PaintUpload _upload;

    private ExploreLayer? _layer;

    /// <summary>
    /// The GPU canvas the drawing is on, shaded there or gone up into it a region at a time, for the
    /// overview's copy once it has arrived; or null where the device was lost on the way.
    /// </summary>
    private CanvasBitmap? _canvas;

    private ExploreLanding(ExploreLayers layers, bool onTop, Action<ExploreSurface> arrived, Motion fade, PaintUpload upload)
    {
        _layers = layers;
        _onTop = onTop;
        _arrived = arrived;
        _fade = fade;
        _upload = upload;
    }

    /// <summary>The layer <see cref="Begin"/> took, which every later call lands in.</summary>
    private ExploreLayer Layer =>
        _layer ?? throw new InvalidOperationException("A redraw lands only once it has begun.");

    /// <summary>
    /// Where a redraw of the picture now lands in <paramref name="layers"/> to replace the drawing the
    /// map works from: over everything, and the drawing worked from once it has landed, when
    /// <paramref name="arrived"/> is told.
    /// </summary>
    /// <param name="fade">
    /// How it comes in over the one it replaces. Unseen while it lands and faded in once all of it has,
    /// for the same picture in a new look, so the old look turns into the new one rather than giving way
    /// to it a region at a time. <see cref="Motion.Instant"/> lands it as it is painted.
    /// </param>
    /// <param name="upload">What it goes up to the GPU through, where it is painted on the CPU: the map's own.</param>
    public static ICanvasRedrawTarget Replacement(
        ExploreLayers layers,
        Action<ExploreSurface> arrived,
        Motion fade,
        PaintUpload upload) =>
        new ExploreLanding(layers, onTop: true, arrived, fade, upload);

    /// <summary>
    /// Where a redraw of the whole picture lands in <paramref name="layers"/> under a zoomed drawing on
    /// top: in its place among the rest by how far it is zoomed, as it is painted, so a move has something
    /// to show wherever it goes. See <see cref="ExploreLayers.LacksWhole"/>.
    /// </summary>
    /// <param name="upload">What it goes up to the GPU through, where it is painted on the CPU: the map's own.</param>
    public static ICanvasRedrawTarget Underlay(ExploreLayers layers, PaintUpload upload) =>
        new ExploreLanding(layers, onTop: false, static _ => { }, Motion.Instant, upload);

    public void Begin(CanvasRedraw redraw, ExploreSurface surface) =>
        _layer = _layers.StartLanding(surface, _onTop, _fade);

    public void Land(CanvasRedraw redraw, byte[] pixels, IReadOnlyList<CanvasRegion> regions) =>
        _canvas = Layer.Land(_upload, pixels, regions);

    public void Shade(CanvasRedraw redraw, CushionQuads quads, TileColour ground) =>
        _canvas = Layer.Shade(quads, ground);

    public void Arrive(CanvasRedraw redraw, ExploreSurface surface)
    {
        _layers.FinishLanding(Layer, surface, _canvas, _onTop, _fade);
        _arrived(surface);
    }

    public void Withdraw(CanvasRedraw redraw) => Layer.Withdraw();
}
