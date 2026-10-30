using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>What shows one tile: a visual painted by a surface of a tile's size, and the tile it holds now.</summary>
internal sealed class TileSprite : IDisposable
{
    private readonly CompositionSurfaceBrush _brush;

    public TileSprite(Compositor compositor, CompositionDrawingSurface surface)
    {
        Surface = surface;
        _brush = compositor.CreateUnstretchedBrush(surface);
        Visual = compositor.CreateSpriteVisual();
        Visual.Brush = _brush;
    }

    public CompositionDrawingSurface Surface { get; }

    public SpriteVisual Visual { get; }

    public MapTile? Tile { get; set; }

    /// <summary>Whether <see cref="Tile"/> has come in in full, and so answers the pointer.</summary>
    public bool Answers { get; set; }

    public void Dispose()
    {
        Visual.Dispose();
        _brush.Dispose();
        Surface.Dispose();
    }
}
