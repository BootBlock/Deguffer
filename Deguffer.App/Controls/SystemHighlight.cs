using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The high contrast theme's colours for what is drawn over the map: the ground the lines are drawn
/// against, and the colours it gives what is selected and what the pointer is over.
/// </summary>
internal readonly record struct SystemHighlight(Color Ground, Color Picked, Color Hovered);
