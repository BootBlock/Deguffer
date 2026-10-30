namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// How a label's opacity follows the magnification of its picture along one axis, where one is the
/// magnification it was drawn at: none at or below <paramref name="Gone"/>, all of it at or above
/// <paramref name="Whole"/>, and in proportion between. <paramref name="Whole"/> is never above
/// <see cref="ExploreLabel.AtRest"/>, so a label is whole where its drawing put it, and always above
/// <paramref name="Gone"/>.
/// </summary>
public readonly record struct LabelFade(float Gone, float Whole);
