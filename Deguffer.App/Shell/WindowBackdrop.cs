using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Deguffer.App.Shell;

/// <summary>
/// Applies the §6.5 Acrylic backdrop, and takes it away again when it would harm legibility.
///
/// Windows already drops the material under battery saver, with transparency effects off, and
/// over Remote Desktop — that fallback is the system's job, and the UI is built to read on the
/// solid colour it falls back to. The one case Windows will not handle for us is high contrast,
/// where translucency actively fights the user's stated requirement, so the backdrop is removed
/// outright.
/// </summary>
public sealed class WindowBackdrop
{
    private readonly Window _window;
    private readonly DesktopAcrylicBackdrop _acrylic = new();
    private bool? _applied;

    public WindowBackdrop(Window window)
    {
        _window = window;

        // XAML raises a theme change when high contrast is switched on or off, which saves
        // pumping WM_SETTINGCHANGE ourselves. It also fires on every light/dark switch, hence
        // the no-op guard in Apply.
        if (window.Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => Apply();
        }
    }

    public void Apply()
    {
        var wanted = !HighContrast.IsEnabled();
        if (_applied == wanted)
        {
            return;
        }

        _applied = wanted;
        _window.SystemBackdrop = wanted ? _acrylic : null;
    }
}
