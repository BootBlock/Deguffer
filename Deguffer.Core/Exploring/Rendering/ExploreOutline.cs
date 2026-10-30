namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// One closed boundary of one node's shape, for a caller that wants to draw a line round it.
///
/// <para>An outline rather than a filled shape, because the point of it is to say which shape is
/// selected without hiding what the shape says. A wash over the top would change the colour the
/// picture spent its whole palette establishing.</para>
///
/// <para>A polygon rather than a rectangle or a sector, so the shell has one thing to draw for all
/// three views. A sunburst's arcs come out as enough short segments that the curve reads as a
/// curve — a hundredth of a pixel from true at the radii drawn here — and the alternative is a
/// shape union the shell would have to take apart again.</para>
///
/// <para><b>One boundary, not one shape.</b> A ring has two, an inner and an outer, and joining
/// them would draw a radial line across it that is not there. So a node can come back more than
/// once, which is also why each of these names the node it belongs to: the answer is a partial
/// mapping — a node this drawing did not draw is simply absent — and without the name a caller
/// cannot tell which shape it was handed, nor a test tell a right answer from a wrong one.</para>
/// </summary>
public readonly record struct ExploreOutline(int Node, IReadOnlyList<ExplorePoint> Points);
