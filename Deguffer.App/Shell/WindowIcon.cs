using Microsoft.UI.Xaml;

namespace Deguffer.App.Shell;

/// <summary>
/// Puts the application mark on a window's title bar and its taskbar entry.
///
/// The manifest icon covers the executable in Explorer, but an unpackaged WinUI window still
/// starts with the generic placeholder until it is told otherwise, so every window Deguffer
/// opens has to ask for this.
/// </summary>
public static class WindowIcon
{
    private static readonly string IconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Deguffer.ico");

    public static void Apply(Window window) => window.AppWindow.SetIcon(IconPath);
}
