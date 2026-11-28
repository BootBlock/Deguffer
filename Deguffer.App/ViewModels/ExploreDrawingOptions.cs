using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the Explore page's header offers beside its view switch, as the view, the colouring, the
/// Files listing and the tree on screen decide it. Each answer is <see cref="ExploreDisplay"/>'s.
///
/// <para>Separate from <see cref="ExploreViewModel"/> because the header reads nothing else of the
/// page, and each value is said to the header only when it changes: the page hands over a display on
/// every snapshot of a running scan.</para>
/// </summary>
public sealed partial class ExploreDrawingOptions : ObservableObject
{
    /// <summary>Whether to offer the map appearance window. See <see cref="ExploreDisplay.ShowsAppearance"/>.</summary>
    [ObservableProperty]
    public partial bool ShowsAppearance { get; private set; } = true;

    /// <summary>What the Colour by box changes in the view on screen. See <see cref="ExploreDisplay.ColouringHint"/>.</summary>
    [ObservableProperty]
    public partial string ColouringHint { get; private set; } = string.Empty;

    /// <summary>Take the answers <paramref name="display"/> gives.</summary>
    internal void Follow(ExploreDisplay display)
    {
        ShowsAppearance = display.ShowsAppearance;
        ColouringHint = display.ColouringHint;
    }
}
