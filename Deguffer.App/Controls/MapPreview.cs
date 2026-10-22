using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Controls;

/// <summary>
/// A small map beside the appearance choices, so the reader sees what a choice does without leaving
/// to look at Explore. Drawn by the map Explore draws with, from <see cref="MapSample"/>, which names
/// nothing on the machine.
///
/// <para>Each change of look on the same view fades from the old look into the new, and a change of
/// spacing moves the shapes to their new places (<see cref="ExploreMap.Restyle"/>). The picture is to be looked at and nothing more: it takes no
/// pointer, no keyboard and no zoom, and assistive technology skips it, because the choices beside it
/// already say in words what it shows.</para>
/// </summary>
public sealed class MapPreview : UserControl
{
    private readonly ExploreMap _map = new()
    {
        Zoomable = false,
        IsHitTestVisible = false,
        IsTabStop = false,
    };

    /// <summary>The view the sample is drawn in, or null before it is first drawn.</summary>
    private ExploreView? _view;

    public MapPreview()
    {
        IsTabStop = false;
        Content = _map;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_map, AccessibilityView.Raw);
    }

    /// <summary>
    /// Draw the sample as <paramref name="look"/> would draw it in <paramref name="view"/>, coloured as
    /// near to <paramref name="colouring"/> as the sample can be (<see cref="MapSample.ColoursFor"/>).
    /// </summary>
    public void Show(MapLook look, ExploreView view, ExploreColouring colouring = ExploreColouring.Branch)
    {
        ArgumentNullException.ThrowIfNull(look);

        var drawn = MapLook.Drawn(view);
        var colours = MapSample.ColoursFor(colouring, look.SchemeFor(drawn));

        if (_view == drawn)
        {
            _map.Restyle(_ => colours, look);
            return;
        }

        var tree = MapSample.Tree;

        _view = drawn;
        _map.Show(tree, tree.RootNode, drawn, _ => colours, ExploreMap.NamedAndSized(tree), look, VolumeSpace.None);
    }
}
