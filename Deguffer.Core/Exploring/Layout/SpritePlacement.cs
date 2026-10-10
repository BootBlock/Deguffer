namespace Deguffer.Core.Exploring.Layout;

/// <summary>Where a sprite's top-left corner is on the canvas, in pixels, and how much it is scaled on each axis.</summary>
public readonly record struct SpritePlacement(double X, double Y, double ScaleX, double ScaleY);
