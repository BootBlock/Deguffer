using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The folder a change of folder left, as it was on screen: the picture of it, the zoom it was at, the
/// drawing the map worked from, if one had arrived, and the colours its drawings were made in. Kept so
/// a reader who asks for it back before the flight has arrived is flown back from where the flight is.
/// See <see cref="FolderFlight"/>.
/// </summary>
public sealed record FolderLeft(MapPicture Picture, MapViewport Viewport, ExploreSurface? Drawing, ShapeColours? Colours);
