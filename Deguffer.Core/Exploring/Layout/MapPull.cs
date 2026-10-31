namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One step of a mouse dragging the picture (<see cref="MapStretch.Move"/>): the tracker is asked to
/// <paramref name="Tracker"/>, which is inside its limits, and the camera is stretched
/// (<paramref name="StretchX"/>, <paramref name="StretchY"/>) pixels from it to where the drag has
/// the picture. The screen shows <paramref name="Shown"/>, and the camera is going to
/// <paramref name="Target"/>, which is that held inside the picture.
///
/// <para>The tracker holds its position to its limits and reports a drag past an edge as held at the
/// edge, so the stretch past it is the camera's own, and while a mouse holds the picture the drag,
/// not the tracker, says what the screen shows: a click is resolved against what the camera shows
/// (§7.1).</para>
/// </summary>
public readonly record struct MapPull(MapTracking Tracker, double StretchX, double StretchY, MapViewport Shown, MapViewport Target);
