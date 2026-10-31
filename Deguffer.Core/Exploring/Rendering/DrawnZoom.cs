using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Where a drawing that can be zoomed was drawn, and how far the map can zoom while it is on screen:
/// the two things only such a drawing says, so one is never there without the other. See
/// <see cref="ExploreSurface.Zoom"/>.
/// </summary>
/// <param name="Viewport">The part of the whole picture the drawing shows.</param>
/// <param name="Ceiling">
/// How far the map can zoom while the drawing is on screen. Said by the drawing because only it knows
/// what in view it could not draw one by one, and how small what it drew is, and what it drew is what
/// the screen shows: a ceiling worked out from anything else would stop a zoom short of detail the
/// reader can see is there, or let it run on past the last shape (see <see cref="MapCeiling"/>).
/// </param>
public sealed record DrawnZoom(MapViewport Viewport, double Ceiling);
