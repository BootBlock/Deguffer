using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One shape travelling from where one view draws it to where another does (<see cref="ViewMorph"/>):
/// a rectangle becoming a bar or a wedge, or the other way round, in one colour all the way.
/// </summary>
/// <param name="Colour">What the shape is painted in throughout: the colour the view it goes to paints it in.</param>
/// <param name="From">Its outline in the view it leaves, traced as <see cref="MorphOutline"/> says.</param>
/// <param name="To">Its outline in the view it goes to, traced the same way, so the two blend point by point.</param>
public readonly record struct ShapeMorph(int Node, TileColour Colour, IReadOnlyList<ExplorePoint> From, IReadOnlyList<ExplorePoint> To);
