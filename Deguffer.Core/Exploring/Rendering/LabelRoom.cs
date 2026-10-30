namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The rectangle a label's text has to stay inside, in canvas pixels. See
/// <see cref="ExploreLabel.Room"/>.
/// </summary>
public readonly record struct LabelRoom(float Left, float Top, float Right, float Bottom);
