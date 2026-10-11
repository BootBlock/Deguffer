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
/// The edges every quad of a drawing in <see cref="ContrastColours"/> is drawn with, in place of its
/// cushion: <paramref name="Width"/> pixels inside each of its four sides, in <paramref name="Colour"/>,
/// with the quad's own colour flat between them.
///
/// <para>Inside rather than across the edge, so each shape draws only its own pixels and the quads
/// still claim the canvas as they do with cushions: two siblings that share an edge draw a line twice
/// as wide between them, and a folder's line stays apart from the lines of what it holds by the gap the
/// layout leaves round them.</para>
/// </summary>
public readonly record struct QuadEdges(TileColour Colour, int Width);
