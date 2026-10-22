namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The two colours of a high contrast theme the map is drawn in, in place of every colouring, cushion
/// and light: the window's colour, which fills the ground and every shape, and the window's text
/// colour, which draws every shape's edge and every name.
///
/// <para>A reader who turns high contrast on has chosen a screen of a few colours they can tell
/// apart, and a map shaded in hundreds of others is the one part of the window that ignores the
/// choice. So the shapes are told apart by their edges rather than by their colours, and what the
/// colours said (a branch, an age, a growth, a kind) is left to the line under the map and the list
/// view, which say it in words (§6.5).</para>
/// </summary>
/// <param name="Window">The theme's window colour: the ground, and the fill of every shape.</param>
/// <param name="Text">The theme's text colour on that window: every shape's edge, and every name.</param>
public readonly record struct ContrastColours(TileColour Window, TileColour Text);

/// <summary>
/// The line round one shape a change of view carries: <paramref name="Width"/> pixels inside its sides,
/// in <paramref name="Colour"/>. What <see cref="ShapeFinish.EdgesOf"/> gives a shape of its drawing,
/// so the shape travels with the line the picture draws round it.
/// </summary>
public readonly record struct QuadEdges(TileColour Colour, int Width);
