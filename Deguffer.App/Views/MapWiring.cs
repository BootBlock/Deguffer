using Deguffer.App.Controls;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;

namespace Deguffer.App.Views;

/// <summary>
/// Joins a page's map to its view-model, the same way on every page that draws one: Explore's drive and
/// Memory's physical memory.
/// </summary>
internal static class MapWiring
{
    /// <summary>
    /// Send what happens on <paramref name="map"/> to <paramref name="navigator"/>, and keep the
    /// reader's choice to hide the map's overview in <paramref name="overviewHidden"/>.
    /// </summary>
    /// <param name="overviewHidden">
    /// The page's setting for the overview. Read once, here, and written as the reader changes it on
    /// the overview itself, which has already shown the change.
    /// </param>
    public static void Attach(ExploreMap map, IMapNavigator navigator, DrawnSetting<bool> overviewHidden)
    {
        map.Hovered += (_, hit) => navigator.Hover(hit);
        map.Activated += (_, node) => navigator.Descend(node);
        map.Ascending += (_, _) => navigator.Ascend();
        map.Picked += (_, node) => navigator.Pick(node);
        map.Facts = navigator.ShapeFacts;

        map.OverviewHidden = overviewHidden.Stored;
        map.OverviewHiddenChanged += (_, _) => overviewHidden.Choose(map.OverviewHidden);
    }
}
