using System.Runtime.InteropServices;
using Deguffer.Core.Configuration;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Deguffer.App.Shell;

/// <summary>
/// Places the window where the last session left it, and gives it a deliberate size when there is
/// no such place.
///
/// WinUI hands an unsized window whatever the platform feels like — in practice a very wide
/// rectangle scaled from the display, which left Deguffer's content stretched across a line length
/// nothing in it needed. The numbers below are chosen for the content: wide enough for the rail
/// plus a finding row with its size column, and no wider. They answer the first launch only; after
/// that <see cref="WindowMetrics"/> answers instead.
///
/// The minimum size needs a window procedure because <see cref="AppWindow"/> exposes no such
/// property; below it the rail and the command bar start colliding.
///
/// The minimum width is set by the compact finding row, which is the shipped view. That row puts
/// the name, the tier chip and the size on one line, and the name is the only part with no fixed
/// demand, so it is what gives way. At the old 720 the rail, the card and the right-hand columns
/// left it about seventy pixels and every name trimmed to an ellipsis — a list of rows that no
/// longer said which row they were.
/// </summary>
public sealed class WindowSizing
{
    private const int DefaultWidth = 1000;
    private const int DefaultHeight = 700;
    private const int MinimumWidth = 880;
    private const int MinimumHeight = 520;

    private const int GwlpWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;

    // The delegate is what the OS holds a raw pointer to. Letting it be collected while the window
    // is alive is an immediate crash on the next message, so it is rooted here for the window's
    // lifetime rather than being a local.
    private readonly WndProc _replacement;
    private readonly nint _original;
    private readonly nint _hwnd;
    private readonly WindowMetricsStore _store;

    /// <summary>
    /// The window's restored rectangle, tracked as it moves rather than read once at close.
    ///
    /// A maximised window reports its maximised rectangle, and by then the rectangle worth
    /// remembering is gone. See <see cref="WindowMetrics.Bounds"/> for why that one is no
    /// substitute for it.
    /// </summary>
    private WindowBounds _restored;

    private bool _maximized;

    public WindowSizing(Window window, WindowMetricsStore store)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _replacement = OnMessage;
        _original = SetWindowProc(_hwnd, Marshal.GetFunctionPointerForDelegate(_replacement));
    }

    /// <summary>
    /// Put the window back where it was left, or size it to its default and centre it on the
    /// display it opened on where nothing was left.
    /// </summary>
    public void Apply()
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        var stored = _store.Load();

        // The display the stored placement sits on, which need not be the one WinUI opened the
        // window on. Nearest covers that display having been unplugged since: the placement is then
        // pulled onto whichever display is closest to where it used to be.
        var display = stored is null
            ? DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromPoint(
                new PointInt32(stored.Bounds.X, stored.Bounds.Y), DisplayAreaFallback.Nearest);

        var area = display.WorkArea;
        var work = new WindowBounds(area.X, area.Y, area.Width, area.Height);

        // The window's current DPI rather than the destination display's. The default size and the
        // floor are both content measurements, and this is the scale the message handler below
        // applies to the same floor once the window has settled.
        var scale = GetDpiForWindow(_hwnd) / 96.0;

        var wanted = stored?.Bounds ?? Centred(work, Scale(DefaultWidth, scale), Scale(DefaultHeight, scale));

        _restored = wanted.Within(work, Scale(MinimumWidth, scale), Scale(MinimumHeight, scale));
        _maximized = stored?.IsMaximized ?? false;

        appWindow.MoveAndResize(
            new RectInt32(_restored.X, _restored.Y, _restored.Width, _restored.Height));

        // Subscribed after the move, so the placement just applied is not read straight back
        // through the handler before the fields above have described it.
        appWindow.Changed += OnWindowChanged;

        if (_maximized && appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    /// <summary>
    /// Write the current placement to disk.
    ///
    /// Called as the window closes, and again by <see cref="ElevatedRelaunch"/> before it starts a
    /// replacement process. The replacement reads this file as it opens, so leaving the write to
    /// the close that follows would leave the two instances racing over it.
    /// </summary>
    public void Remember() => _store.Save(new WindowMetrics(_restored, _maximized));

    /// <summary>
    /// Follow the window as the user moves, resizes and maximises it.
    ///
    /// Minimised is passed over on purpose. It is neither a placement to restore to nor a reason to
    /// forget that the window was maximised before it was minimised.
    /// </summary>
    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange)
        {
            return;
        }

        switch ((sender.Presenter as OverlappedPresenter)?.State)
        {
            case OverlappedPresenterState.Restored:
                _restored = new WindowBounds(
                    sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
                _maximized = false;
                break;

            case OverlappedPresenterState.Maximized:
                _maximized = true;
                break;
        }
    }

    /// <summary>
    /// A window of this size in the middle of <paramref name="work"/>.
    ///
    /// Clamped to the work area first: on a small or scaled display the preferred size can exceed
    /// the screen, and a window taller than the desktop cannot be resized back by dragging.
    /// </summary>
    private static WindowBounds Centred(WindowBounds work, int width, int height)
    {
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);

        return new WindowBounds(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height);
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmGetMinMaxInfo)
        {
            // MINMAXINFO is in physical pixels, so the floor scales with the display the window
            // is currently on — dragging to a 200% monitor must not shrink the usable layout.
            var scale = GetDpiForWindow(hwnd) / 96.0;
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);

            info.MinTrackSize.X = Scale(MinimumWidth, scale);
            info.MinTrackSize.Y = Scale(MinimumHeight, scale);

            Marshal.StructureToPtr(info, lParam, false);
        }

        return CallWindowProc(_original, hwnd, message, wParam, lParam);
    }

    private static int Scale(int value, double scale) => (int)Math.Round(value * scale);

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    /// <summary>
    /// 32-bit user32 has no <c>SetWindowLongPtrW</c> — there it is a macro over
    /// <c>SetWindowLongW</c>, and binding the Ptr name would fail to resolve at runtime. x86 is a
    /// supported platform here (§6.3 ships per-architecture), so both are bound.
    /// </summary>
    private static nint SetWindowProc(nint hWnd, nint value) => nint.Size == 8
        ? SetWindowLongPtr(hWnd, GwlpWndProc, value)
        : SetWindowLong(hWnd, GwlpWndProc, value.ToInt32());

    // DllImport rather than LibraryImport, matching HighContrast: the generator wants
    // AllowUnsafeBlocks across the whole project, which is a large blast radius for these calls.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern nint SetWindowLong(nint hWnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint previous, nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);
}
