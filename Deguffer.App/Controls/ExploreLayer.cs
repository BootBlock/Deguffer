using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace Deguffer.App.Controls;

/// <summary>
/// One drawing of a map in the composition tree: a sprite painted by a surface, the drawing in it or
/// on its way into it, and where in the whole picture it lies. See <see cref="ExploreLayers"/>, which
/// decides which drawing goes in which.
///
/// <para>Made once and written through. A new drawing of another size resizes the surface rather than
/// making another, and a layer nothing is using is emptied rather than thrown away (G5). Only a
/// replaced device makes it take another surface: see <see cref="Renew"/>.</para>
/// </summary>
internal sealed class ExploreLayer
{
    private readonly Compositor _compositor;

    private readonly DispatcherQueue _dispatcher;

    private readonly MapGraphics _graphics;

    private readonly CompositionSurfaceBrush _brush;

    private CompositionVirtualDrawingSurface _surface;

    /// <summary>The pointer's light on the canvas, where it was shaded on the GPU.</summary>
    private readonly CanvasLight _light = new();

    public ExploreLayer(Compositor compositor, MapGraphics graphics)
    {
        _compositor = compositor;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _graphics = graphics;
        _surface = graphics.CreateSurface(1, 1);

        // The sprite's own scale is what places the canvas in the picture.
        _brush = compositor.CreateUnstretchedBrush(_surface);

        Sprite = compositor.CreateSpriteVisual();
        Sprite.Brush = _brush;
        Sprite.IsVisible = false;
    }

    public SpriteVisual Sprite { get; }

    /// <summary>
    /// What the canvas is painted on. Another for each replaced device (see <see cref="Renew"/>), so
    /// asked for each time rather than kept.
    /// </summary>
    public ICompositionSurface Surface => _surface;

    /// <summary>The canvas the surface holds, in pixels.</summary>
    public SizeInt32 Size { get; private set; } = new(1, 1);

    /// <summary>The drawing on the surface, once all of it is there, or null.</summary>
    public ExploreSurface? Drawing { get; set; }

    /// <summary>The redraw landing on the surface, while it does, or null.</summary>
    public CanvasRedraw? Landing { get; set; }

    /// <summary>
    /// Whether the redraw landing here replaces the drawing on top, and so shows over it while it
    /// lands, rather than going under it.
    /// </summary>
    public bool LandsOnTop { get; set; }

    /// <summary>The part of the picture the drawing shows, or will once it has landed.</summary>
    public MapViewport Viewport { get; set; }

    /// <summary>Which picture the drawing is of, on the count <see cref="ExploreLayers"/> keeps.</summary>
    public int Picture { get; set; }

    /// <summary>When it was last shown, on the count <see cref="ExploreLayers"/> keeps.</summary>
    public long LastShown { get; set; }

    /// <summary>Whether anything of a drawing is on the surface to be seen.</summary>
    public bool IsShown => Drawing is not null || Landing is not null;

    /// <summary>
    /// Get ready for a canvas <paramref name="width"/> by <paramref name="height"/> to land a region
    /// at a time: nothing of it shows, so whatever is under it shows until each region lands, rather
    /// than whatever the surface held last.
    /// </summary>
    public void Prepare(int width, int height)
    {
        MapGraphics.Empty(_surface);
        _light.Forget();

        if (Size.Width != width || Size.Height != height)
        {
            Size = new SizeInt32(width, height);
            _surface.Resize(Size);
            Sprite.Size = new Vector2(width, height);
        }
    }

    /// <summary>
    /// Write <paramref name="regions"/> of <paramref name="pixels"/>, a canvas this surface's size,
    /// and nothing else of it: the rest of the buffer is still being painted.
    /// </summary>
    public void Land(byte[] pixels, IReadOnlyList<CanvasRegion> regions)
    {
        foreach (var region in regions)
        {
            _graphics.Write(_surface, pixels, Size.Width, region);
        }
    }

    /// <summary>Shade the whole canvas, a canvas this surface's size, from <paramref name="quads"/> on <paramref name="ground"/>.</summary>
    public void Shade(CushionQuads quads, TileColour ground)
    {
        _graphics.Shade(_surface, quads, ground);
        AfterCommit(_light.Shaded(quads, ground));
    }

    /// <summary>
    /// Put the pointer's light at <paramref name="light"/> on a canvas shaded on the GPU, or take it off
    /// with null, once the compositor has taken the surface: see <see cref="CanvasLight"/>. Nothing on a
    /// canvas painted on the CPU, which has nothing to shade it again from.
    /// </summary>
    public void Light(PointerLight? light)
    {
        if (_light.Want(light) is { } commit)
        {
            AfterCommit(commit);
        }
    }

    /// <summary>
    /// Put the canvas where its part of the picture is, in a picture laid out over a screen
    /// <paramref name="width"/> by <paramref name="height"/> and measured from
    /// <paramref name="origin"/>. Only a resize, a new drawing or a new origin moves it: the camera
    /// above it is what moves as the picture does.
    /// </summary>
    public void Fit(double width, double height, MapOrigin origin)
    {
        var placed = Viewport.Canvas(Size.Width, Size.Height, width, height, origin);

        Sprite.Place(placed);
    }

    /// <summary>Let go of the drawing and of everything on the surface, keeping the layer for the next one.</summary>
    public void Empty()
    {
        Drawing = null;
        Landing = null;
        Sprite.IsVisible = false;

        MapGraphics.Empty(_surface);
        _light.Forget();
    }

    /// <summary>
    /// Take a new surface, of the same size, for a device that has replaced the one this was
    /// written through. What the old one held is gone, and writing into it again put nothing on
    /// screen: see <see cref="MapGraphics.Replaced"/>.
    /// </summary>
    public void Renew()
    {
        Drawing = null;
        Landing = null;
        Sprite.IsVisible = false;

        _surface = _graphics.CreateSurface(Size.Width, Size.Height);
        _brush.Surface = _surface;
        _light.Forget();
    }

    /// <summary>Ask the compositor for <paramref name="commit"/>, and shade the light it settles once it is done.</summary>
    private void AfterCommit(int commit) =>
        _compositor.RequestCommitAsync().Completed = (_, _) => _dispatcher.TryEnqueueGuarded(() =>
        {
            if (_light.Committed(commit) is { } shading)
            {
                _graphics.Shade(_surface, shading.Quads, shading.Ground, shading.Region, shading.Light);
            }
        });
}
