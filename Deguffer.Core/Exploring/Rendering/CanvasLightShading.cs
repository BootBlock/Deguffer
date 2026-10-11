namespace Deguffer.Core.Exploring.Rendering;

/// <summary>A region of a canvas to shade again from <paramref name="Quads"/> on <paramref name="Ground"/>, under <paramref name="Light"/> where there is one.</summary>
public readonly record struct CanvasLightShading(CushionQuads Quads, TileColour Ground, CanvasRegion Region, PointerLight? Light);
