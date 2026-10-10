using System.Runtime.InteropServices;
using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace Deguffer.App.Shell;

/// <summary>
/// The motion Windows' Animation effects setting asks for, followed while the app runs.
///
/// <para>The framework's theme transitions follow the setting on their own. The map's animations are
/// clocked by the app, so nothing follows it for them but this.</para>
///
/// <para>Asked of the system at every request rather than kept and updated on
/// <c>AnimationsEnabledChanged</c>. In an unpackaged desktop process that event was not raised when
/// the setting was turned off, while the property read the new value at once, so a kept answer would
/// go on animating for a reader who had just turned animation off. The read is cheap enough for every
/// frame of a quarter-second move.</para>
///
/// <para>An animation that runs for as long as its page is open, rather than for a moment, cannot wait
/// for its next request to notice. <see cref="Changed"/> tells it, from the message Windows broadcasts
/// to every top-level window when the setting is changed, which reaches the main window once
/// <see cref="Follow"/> has been called on it.</para>
/// </summary>
internal sealed class SystemMotion : IMotionPolicy
{
    private const uint WmSettingChange = 0x001A;
    private const nint SpiSetClientAreaAnimation = 0x1043;

    /// <summary>Which of the window's subclasses this is, among any others comctl32 holds for it.</summary>
    private const nuint SubclassId = 0x4D4F54;

    public static SystemMotion Current { get; } = new();

    private readonly UISettings _settings = new();

    /// <summary>
    /// What the OS holds a raw pointer to. Collected while the window is alive, the next message would
    /// crash the process, so it is rooted here, for the process's life, as the window is.
    /// </summary>
    private readonly SubclassProc _watch;

    private SystemMotion() => _watch = OnMessage;

    /// <summary>
    /// The reader's Animation effects setting changed, on the UI thread of the window being followed.
    /// Ask <see cref="For"/> again for the new answer.
    /// </summary>
    public event EventHandler? Changed;

    public Motion For(MotionToken token) => token.For(_settings.AnimationsEnabled);

    /// <summary>Hear the setting change through <paramref name="window"/>'s messages.</summary>
    public void Follow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // A subclass rather than a replaced window procedure, so it sits beside WindowSizing's without
        // either needing to know about the other.
        SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(window), _watch, SubclassId, 0);
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmSettingChange && wParam == SpiSetClientAreaAnimation)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);

    // DllImport rather than LibraryImport, matching HighContrast: the generator wants AllowUnsafeBlocks
    // across the whole project, which is a large blast radius for these calls.
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
