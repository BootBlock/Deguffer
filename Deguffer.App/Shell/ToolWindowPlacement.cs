using Deguffer.Core.Configuration;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Deguffer.App.Shell;

/// <summary>
/// Makes a small window belong to the main one, opens it where the reader left it, or beside the main
/// window the first time, and remembers where it is left and whether it was open.
///
/// <para>Belonging is Win32 ownership: the small window stays above the main one, comes forward with
/// it when the main window is activated, goes when it is minimised and comes back with it. WinUI has
/// no property for it, so it is set on the window handle, as <see cref="WindowSizing"/> sets the main
/// window's minimum size. It is left out of the taskbar and the task switcher as well, because it is
/// part of the main window rather than an application of its own.</para>
/// </summary>
internal sealed class ToolWindowPlacement
{
    /// <summary>The gap left between the two windows, in device-independent pixels.</summary>
    private const int Gap = 8;

    /// <summary>
    /// How far below the owner's top the window opens where it has to cover the owner, in
    /// device-independent pixels: past the title bar and the page's toolbar.
    /// </summary>
    private const int Drop = 96;

    private const int GwlpHwndParent = -8;

    private readonly Window _tool;
    private readonly Window _owner;
    private readonly WindowPlacementStore<ToolWindowMetrics> _store;
    private readonly int _width;
    private readonly int _height;
    private readonly int _minimumWidth;
    private readonly int _minimumHeight;

    /// <summary>The window's rectangle as it was last drawn, followed as it moves and resizes.</summary>
    private WindowBounds _bounds;

    /// <param name="width">The width the window opens at the first time, in device-independent pixels.</param>
    /// <param name="height">The height it opens at the first time, in device-independent pixels.</param>
    /// <param name="minimumWidth">How narrow the reader can make it, in device-independent pixels.</param>
    /// <param name="minimumHeight">How short the reader can make it, in device-independent pixels.</param>
    public ToolWindowPlacement(
        Window tool,
        Window owner,
        WindowPlacementStore<ToolWindowMetrics> store,
        int width,
        int height,
        int minimumWidth,
        int minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(store);

        _tool = tool;
        _owner = owner;
        _store = store;
        _width = width;
        _height = height;
        _minimumWidth = minimumWidth;
        _minimumHeight = minimumHeight;
    }

    /// <summary>
    /// Give the window to its owner and put it where it was left, pulled onto a display that is
    /// there now (see <see cref="WindowBounds.Within"/>), or beside the owner where nothing was left
    /// (<see cref="WindowBounds.Beside"/>).
    /// </summary>
    public void Apply()
    {
        var toolHandle = WinRT.Interop.WindowNative.GetWindowHandle(_tool);
        var ownerHandle = WinRT.Interop.WindowNative.GetWindowHandle(_owner);

        _ = WindowLong.Set(toolHandle, GwlpHwndParent, ownerHandle);
        _tool.AppWindow.IsShownInSwitchers = false;

        var stored = _store.Load();
        var owner = BoundsOf(_owner.AppWindow);

        // The display the stored placement sits on, which need not be the owner's. Nearest covers that
        // display having been unplugged since, as it does for the main window.
        var display = stored is null
            ? DisplayArea.GetFromWindowId(_owner.AppWindow.Id, DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromPoint(new PointInt32(stored.Bounds.X, stored.Bounds.Y), DisplayAreaFallback.Nearest);

        var area = display.WorkArea;
        var work = new WindowBounds(area.X, area.Y, area.Width, area.Height);
        var scale = DisplayScale.Of(work, ownerHandle);

        _bounds = stored is null
            ? owner.Beside(
                work,
                DisplayScale.Scale(_width, scale),
                DisplayScale.Scale(_height, scale),
                DisplayScale.Scale(Gap, scale),
                DisplayScale.Scale(Drop, scale))
            : stored.Bounds.Within(work, DisplayScale.Scale(_minimumWidth, scale), DisplayScale.Scale(_minimumHeight, scale));

        Floor(scale);

        _tool.AppWindow.MoveAndResize(new RectInt32(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height));

        // After the move, so the placement just applied is not read straight back before it was described.
        _tool.AppWindow.Changed += OnChanged;
    }

    /// <summary>
    /// Write where the window is now to disk, and whether it is to open again with the application.
    /// </summary>
    public void Remember(bool isOpen) => _store.Save(new ToolWindowMetrics(_bounds, isOpen));

    /// <summary>
    /// Follow the window as the reader moves and resizes it, and keep its floor at the scale of the
    /// display it is on, which a drag to a display at another scale changes.
    ///
    /// <para>Only a window drawn as it is. A minimised owner takes this window with it, and the
    /// rectangle Windows reports for a window that is not on screen is no place to restore to.</para>
    /// </summary>
    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((!args.DidPositionChange && !args.DidSizeChange)
            || !sender.IsVisible
            || (sender.Presenter as OverlappedPresenter)?.State != OverlappedPresenterState.Restored)
        {
            return;
        }

        _bounds = BoundsOf(sender);

        Floor(DisplayScale.Of(Win32Handle(sender)));
    }

    /// <summary>The smallest the reader can make the window, in physical pixels at <paramref name="scale"/>.</summary>
    private void Floor(double scale)
    {
        if (_tool.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = DisplayScale.Scale(_minimumWidth, scale);
            presenter.PreferredMinimumHeight = DisplayScale.Scale(_minimumHeight, scale);
        }
    }

    private static WindowBounds BoundsOf(AppWindow window) =>
        new(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);

    private static nint Win32Handle(AppWindow window) => Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id);
}
