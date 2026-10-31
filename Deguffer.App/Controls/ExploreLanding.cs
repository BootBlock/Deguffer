using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Controls;

/// <summary>
/// One redraw landing in one of a map's layers (<see cref="ExploreLayers"/>): the layer it took, and
/// what it does once it has arrived. See <see cref="ICanvasRedrawTarget"/> for the order of the calls.
/// </summary>
/// <param name="onTop">
/// Whether it replaces the drawing the map works from, or lands under it in its place by how far it is
/// zoomed. See <see cref="ExploreLayers.Replacement"/> and <see cref="ExploreLayers.Underlay"/>.
/// </param>
/// <param name="arrived">Told once all of the drawing has landed.</param>
/// <param name="fade">How a drawing on top comes in over the one it replaces.</param>
internal sealed class ExploreLanding(ExploreLayers layers, bool onTop, Action<ExploreSurface> arrived, Motion fade)
    : ICanvasRedrawTarget
{
    private ExploreLayer? _layer;

    /// <summary>The layer <see cref="Begin"/> took, which every later call lands in.</summary>
    private ExploreLayer Layer =>
        _layer ?? throw new InvalidOperationException("A redraw lands only once it has begun.");

    public void Begin(CanvasRedraw redraw, ExploreSurface surface) =>
        _layer = layers.StartLanding(surface, onTop, fade);

    public void Land(CanvasRedraw redraw, byte[] pixels, IReadOnlyList<CanvasRegion> regions) =>
        Layer.Land(pixels, regions);

    public void Shade(CanvasRedraw redraw, CushionQuads quads, TileColour ground) =>
        Layer.Shade(quads, ground);

    public void Arrive(CanvasRedraw redraw, ExploreSurface surface)
    {
        layers.FinishLanding(Layer, surface, redraw.Pixels, onTop, fade);
        arrived(surface);
    }

    public void Withdraw(CanvasRedraw redraw) => Layer.Withdraw();
}
