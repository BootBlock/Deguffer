using System.Runtime.InteropServices;

namespace Deguffer.Core.Viewing;

/// <summary>
/// The Windows calls <see cref="WindowCover"/> makes about the desktop's top-level windows, as a seam a
/// test can drive. This machine's desktop cannot be made to hold a see-through window in front of
/// Deguffer's, a cloaked one, or a monitor layout of a test's choosing.
/// </summary>
internal interface IWindowStack
{
    /// <summary>Every top-level window, the front one first, or null where Windows would not enumerate them.</summary>
    IReadOnlyList<nint>? FrontToBack();

    /// <summary>Every monitor's rectangle, or null where Windows would not list them.</summary>
    IReadOnlyList<ScreenRect>? Screens();

    /// <summary>
    /// Where the window's frame is drawn, without the invisible border it is resized by or its shadow,
    /// or null where the compositor would not say.
    /// </summary>
    ScreenRect? Frame(nint window);

    /// <summary>Whether it and every ancestor is visible. A window that has gone is not.</summary>
    bool IsVisible(nint window);

    /// <summary>Whether it is minimised.</summary>
    bool IsMinimised(nint window);

    /// <summary>
    /// Whether the compositor is holding it off the screen, as the shell holds a window on another
    /// virtual desktop, or null where the compositor would not say.
    /// </summary>
    bool? IsCloaked(nint window);

    /// <summary>
    /// Whether it paints every pixel of its frame, so nothing behind it shows through: not
    /// click-through, not see-through, not cut to a shape. False where any of that cannot be read.
    /// </summary>
    bool IsSolid(nint window);
}

/// <inheritdoc />
internal sealed partial class WindowStack : IWindowStack
{
    public static readonly WindowStack Instance = new();

    private const int ExtendedStyle = -20;
    private const uint Layered = 0x00080000;
    private const uint ClickThrough = 0x00000020;
    private const uint ColourKey = 0x1;
    private const uint Alpha = 0x2;
    private const int ExtendedFrameBounds = 9;
    private const int Cloaked = 14;

    /// <summary><c>GetWindowRgnBox</c>'s answer for a window with no shape of its own, or for a call that failed.</summary>
    private const int NoRegion = 0;

    private WindowStack()
    {
    }

    /// <summary>
    /// <c>EnumWindows</c> lists top-level windows in z-order, front first, which the browsers' own
    /// occlusion trackers rely on too. The <c>GetWindow</c> walk documented as z-order is not used,
    /// because Windows warns that it can loop for ever when a window moves while it runs.
    /// </summary>
    public IReadOnlyList<nint>? FrontToBack()
    {
        var windows = new List<nint>();

        return EnumWindows(
            (window, _) =>
            {
                windows.Add(window);
                return true;
            },
            0)
            ? windows
            : null;
    }

    public IReadOnlyList<ScreenRect>? Screens()
    {
        var screens = new List<ScreenRect>();

        return EnumDisplayMonitors(
            0,
            0,
            (_, _, ref rect, _) =>
            {
                screens.Add(rect.ToScreen());
                return true;
            },
            0)
            ? screens
            : null;
    }

    public ScreenRect? Frame(nint window) =>
        DwmGetWindowAttribute(window, ExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) >= 0 ? frame.ToScreen() : null;

    public bool IsVisible(nint window) => IsWindowVisible(window);

    public bool IsMinimised(nint window) => IsIconic(window);

    public bool? IsCloaked(nint window) =>
        DwmGetWindowAttribute(window, Cloaked, out int cloak, sizeof(int)) >= 0 ? cloak != 0 : null;

    /// <summary>
    /// A layered window is solid only where its whole-window alpha is opaque and it keys no colour out.
    /// One that draws its own per-pixel alpha, as an overlay does, gives no attributes at all, and is
    /// read as see-through. A window with a region of its own is cut to that shape, and a click-through
    /// one is an overlay by intent, so neither is solid.
    /// </summary>
    public bool IsSolid(nint window)
    {
        Marshal.SetLastSystemError(0);
        var style = (uint)GetWindowLong(window, ExtendedStyle);

        if ((style == 0 && Marshal.GetLastPInvokeError() != 0)
            || (style & ClickThrough) != 0
            || GetWindowRgnBox(window, out _) != NoRegion)
        {
            return false;
        }

        if ((style & Layered) == 0)
        {
            return true;
        }

        return GetLayeredWindowAttributes(window, out _, out var alpha, out var flags)
            && (flags & ColourKey) == 0
            && ((flags & Alpha) == 0 || alpha == byte.MaxValue);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly ScreenRect ToScreen() => new(Left, Top, Right, Bottom);
    }

    /// <summary>
    /// Declared with <c>DllImport</c>, as <c>EnumDisplayMonitors</c> is: the source generator cannot
    /// marshal a managed callback.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumerateWindows callback, nint parameter);

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumerateWindows(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint context, nint clip, EnumerateMonitors callback, nint parameter);

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumerateMonitors(nint monitor, nint context, ref Rect rect, nint parameter);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int length);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int length);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    /// <summary>
    /// <c>GetWindowLongW</c> rather than <c>GetWindowLongPtrW</c>, which the x86 user32 does not export:
    /// the extended style is 32 bits on every platform.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRgnBox(nint window, out Rect box);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLayeredWindowAttributes(nint window, out uint key, out byte alpha, out uint flags);
}
