using System.Runtime.InteropServices;
using Deguffer.Core.Configuration;

namespace Deguffer.App.Shell;

/// <summary>
/// The scale factor of a display, asked of Win32, for a window placed in physical pixels: the main
/// window and the map appearance window both turn sizes chosen in device-independent pixels into
/// physical ones before they place themselves.
/// </summary>
internal static class DisplayScale
{
    private const uint MonitorDefaultToNearest = 0x0002;
    private const int EffectiveDpi = 0;

    /// <summary>
    /// The scale factor of the display <paramref name="work"/> belongs to, or of
    /// <paramref name="window"/>'s own display where that cannot be read.
    ///
    /// <para>Not the window's own first: a window that has not moved yet reports the display WinUI
    /// opened it on, and a placement left on a second display would then be sized against the first
    /// display's scale. On a desktop that mixes scale factors the floor for a 200% display raises a
    /// 1000x700 window on a 100% one to 1760x1040, and that is not self-correcting: the inflated
    /// rectangle is what the close writes back.</para>
    /// </summary>
    public static double Of(WindowBounds work, nint window)
    {
        // The work area's own corner is on the display it describes, and MONITOR_DEFAULTTONEAREST is
        // the fallback DisplayArea is asked for by every caller.
        var monitor = MonitorFromPoint(new Point { X = work.X, Y = work.Y }, MonitorDefaultToNearest);

        return GetDpiForMonitor(monitor, EffectiveDpi, out var dpi, out _) == 0
            ? dpi / 96.0
            : Of(window);
    }

    /// <summary>The scale factor of the display <paramref name="window"/> is on now.</summary>
    public static double Of(nint window) => GetDpiForWindow(window) / 96.0;

    /// <summary><paramref name="value"/> device-independent pixels in physical ones, at <paramref name="scale"/>.</summary>
    public static int Scale(int value, double scale) => (int)Math.Round(value * scale);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    // DllImport rather than LibraryImport, matching HighContrast: the generator wants
    // AllowUnsafeBlocks across the whole project, which is a large blast radius for these calls.
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
