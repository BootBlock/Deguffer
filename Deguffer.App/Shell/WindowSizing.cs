using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Deguffer.App.Shell;

/// <summary>
/// Gives the window a deliberate size instead of the framework's default.
///
/// WinUI hands an unsized window whatever the platform feels like — in practice a very wide
/// rectangle scaled from the display, which left Deguffer's content stretched across a line length
/// nothing in it needed. The numbers below are chosen for the content: wide enough for the rail
/// plus a finding row with its size column, and no wider.
///
/// The minimum size needs a window procedure because <see cref="AppWindow"/> exposes no such
/// property; below it the rail and the command bar start colliding.
/// </summary>
public sealed class WindowSizing
{
    private const int DefaultWidth = 1000;
    private const int DefaultHeight = 700;
    private const int MinimumWidth = 720;
    private const int MinimumHeight = 520;

    private const int GwlpWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;

    // The delegate is what the OS holds a raw pointer to. Letting it be collected while the window
    // is alive is an immediate crash on the next message, so it is rooted here for the window's
    // lifetime rather than being a local.
    private readonly WndProc _replacement;
    private readonly nint _original;
    private readonly nint _hwnd;

    public WindowSizing(Window window)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _replacement = OnMessage;
        _original = SetWindowProc(_hwnd, Marshal.GetFunctionPointerForDelegate(_replacement));
    }

    /// <summary>Size the window to its default and centre it on the display it opened on.</summary>
    public void Apply()
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        var scale = GetDpiForWindow(_hwnd) / 96.0;

        var width = Scale(DefaultWidth, scale);
        var height = Scale(DefaultHeight, scale);

        // Clamp to the work area: on a small or scaled display the preferred size can exceed the
        // screen, and a window taller than the desktop cannot be resized back by dragging.
        var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);

        appWindow.MoveAndResize(new RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));
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
