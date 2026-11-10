using System.Runtime.InteropServices;
using Deguffer.Core.Viewing;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Deguffer.App.Shell;

/// <summary>
/// Where an element is on the desktop, in the physical pixels Windows gives every window's place in, and
/// the top-level window it is in.
/// </summary>
internal readonly record struct ScreenPlace(nint Window, ScreenRect Rect)
{
    /// <summary>
    /// Where <paramref name="element"/> is, or null where it is in no window or Windows would not say
    /// where the window's client area is. The XAML content starts at the client area's corner, title
    /// bar or not, and every pixel the element touches is counted.
    /// </summary>
    public static ScreenPlace? Of(FrameworkElement element)
    {
        if (element.XamlRoot is not { } root)
        {
            return null;
        }

        var window = Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        var corner = default(ClientPoint);

        if (window == 0 || !ClientToScreen(window, ref corner))
        {
            return null;
        }

        var scale = root.RasterizationScale;
        var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        return new ScreenPlace(
            window,
            new ScreenRect(
                corner.X + (int)Math.Floor(bounds.Left * scale),
                corner.Y + (int)Math.Floor(bounds.Top * scale),
                corner.X + (int)Math.Ceiling(bounds.Right * scale),
                corner.Y + (int)Math.Ceiling(bounds.Bottom * scale)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientPoint
    {
        public int X;
        public int Y;
    }

    // DllImport rather than LibraryImport, matching the shell's other calls: the generator wants
    // AllowUnsafeBlocks across the whole project.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref ClientPoint point);
}
