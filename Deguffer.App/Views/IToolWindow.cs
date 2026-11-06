using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Deguffer.App.Views;

/// <summary>
/// A tool window as <see cref="ToolWindowOpener"/> sees it: shown, remembered and closed. A seam so the
/// opener's tests can open and close one without a WinUI window, which a test process cannot build.
/// <see cref="Activate"/> and <see cref="Closed"/> are the <see cref="Window"/>'s own.
/// </summary>
internal interface IToolWindow
{
    event TypedEventHandler<object, WindowEventArgs> Closed;

    /// <summary>Put the window on screen and give it the focus.</summary>
    void Activate();

    /// <summary>Put the window on screen without moving the focus to it.</summary>
    void ShowWithoutFocus();

    /// <summary>Write where the window is to disk, and whether it is to open again with the application.</summary>
    void Remember(bool isOpen);
}
