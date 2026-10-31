using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Controls;

/// <summary>
/// The keyboard landed on <paramref name="Shape"/> of <paramref name="Folder"/> in
/// <paramref name="Drawing"/> (<see cref="ExploreKeyboard.Landed"/>), and picked it where
/// <paramref name="Picks"/> says.
/// </summary>
internal readonly record struct ExploreKeyboardLanding(ExploreSurface Drawing, int? Folder, MapShape Shape, bool Picks);
