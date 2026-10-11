namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// One node's shape in one drawing, as a change of view carries it (<see cref="Layout.ViewMorph"/>).
/// </summary>
/// <param name="Depth">How far below the drawing's root the node is, which orders the shapes as they are painted.</param>
/// <param name="Colour">The colour the drawing paints the shape in.</param>
/// <param name="Outline">The shape's boundary, traced as <see cref="MorphOutline"/> says, in the drawing's canvas pixels.</param>
/// <param name="Edges">
/// The edges the drawing draws the shape with, or null where it shades it: in a high contrast theme's
/// colours every shape is the ground's colour, and only its edge shows it travel.
/// </param>
public readonly record struct MorphShape(int Node, int Depth, TileColour Colour, IReadOnlyList<ExplorePoint> Outline, QuadEdges? Edges = null);
